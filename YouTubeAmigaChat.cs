// ----------------

// == YouTubeChatAmigaLink ==
// Console Application to read chat messages for a YouTube livestream from the Youtube API and pipe them to a serial
// port in a format the Amiga is capable of displaying reasonably well.
// - converts the chat into extended ASCII
// - converts emojis into text
// - YouTube chat can use commands starting with / to create ANSI escape codes .e.g. /r red text /b blue text
// - /say command uses a script in Ncomm to launch amiga say and speak the text following /say. e.g. /say hello, would cause the amgia to speak "hello" out loud if the ncomm script was running.
// - Tested with "Amiga DiagROM Amiga DB25 Serial Port Output Debug Adapter for PC Type C", search eBay.co.uk for the listing.

// Uses Frameworks:
// - Google.Apis.Auth
// - Google.Apis.YouTube.v3
// - EmojiToText

// ----------------

// NComm 3.09 (Updated 22nd October 2026) - https://aminet.net/package/comm/term/ncomm309
// NComm 3.0 (1993 release, lha contains the scripting docs etc) - https://aminet.net/package/comm/term/ncomm30
// ANSI Escape Sequences - https://gist.github.com/fnky/458719343aabd01cfb17a3a4f7296797
// DiagROM USB to DB25 Adaptor - https://www.ebay.co.uk/itm/127118641849

// ----------------

// Amiga OS 3.0 and later didn't come with the say program. This is the Amiga CLI script I used 
// to copy say program from a workbench 2.0 disk onto an Amiga 1200 with Amiga OS 3.2
// @see Amiga/CLI/getspeak
/*
copy df0:utilities/say sys:utilities/ CLONE
copy df0:devs/narrator.device sys:devs/
copy df0:libs/translator.library sys:libs/
copy df0:L/speak-handler sys:L/
*/

// ----------------

// This is the NComm script used to get NComm to speak say commands:
// @see Amiga/NComm/speak.script
/*
;NComm script to speak commands that come in from the PC via serial (from YouTube chat), using the "say" 
;program launched in a CLI script
;v1.02

MESSAGE "ncomm script to listen for lines starting with /say\n\r"
MESSAGE "and sending them to the Amgia CLI say command to\n\r"
MESSAGE "be read out load.\n\r"

label:
INPUT $line "\n\r"	;Input will be placed in the variable 'say'
           		;until carriage return is received.
STRLEN $linelen $line
IF $linelen == "0" THEN GOTO end
IF $linelen == "1" THEN GOTO end
IF $linelen == "2" THEN GOTO end
IF $linelen == "3" THEN GOTO end
IF $linelen == "4" THEN GOTO end
STRPART $startsaycommand 0 4 $line
SET $linelen = $linelen-"4"#
STRPART $texttosay 4 $linelen $line
IF $startsaycommand == "/say" THEN CLI "sys:Utilities/say -p140 "$texttosay

end:
SET $line = ""
GOTO label
*/

// ----------------

using EmojiToText;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;

class YouTubeChatAmigaLink
{
    /** Program version number, printed to the serial number when connected */
    string ProgramVersion = "1.11";

    /** The connection to the serial port on the Amiga */
    SerialPort AmigaSerialPort = new SerialPort();

    /** This contains data that is read from the serial port that hasn't been processed yet */
    string IncommingDataFromSerialPort = "";

    /* When true, we will disable the serial link and still connect to Youtube, used for debugging */
    const bool bDisableSerialLink = false;

    /* When true, show how many milliseconds we are waiting for before polling the next set of messages from YT */
    const bool bShowWaitingMessage = false;

    /* The max number of chars to send at once over the serial line before adding a new line and carrage return */
    const int MaxCharsPerLine = 80;

    /** When true, send a line break over the serial port when sending long messages. False if the target applicaiton suppprts line wrapping. */
    const bool bAddLineWraps = false;

    /** After sending messages to the serial port, wait this many Ms before sending another */
    const int MsSleepBetweenSerialMessageSend = 2;

    /** After getting a page of YouTube chat messages, wait at least this many milliseconds before getting another page (wait may be longer if the YouTube API decrees it) */
    TimeSpan MinWaitBetweenNextPageYouTubeMessages = new TimeSpan(0, 0, 0, 0, 8000);

    /** the max number of messages to reqest from Youtube API in a single command */
    const int MaxYoutubeChatMessagesInOneBatch = 100;

    /** Amiga format new line string */
    const string AmigaNewLine = "\n\r";

    /** Data received will contain this to indicate a new line */
    const string IncomingDataNewLine = "\r";

    /** This command in YT chat lists all the commands available */
    const string ListCommandsCommand = "/listcommands";

    /** This command in YT chat requires the line to be printed starting with /say so ncomm script can read it and send it to CLI Amiga say */
    const string SayCommand = "/say";

    /** Needed to insert this delay after the line break that is sent before a /say command, otherwise the ncomm script isn't able to detect the input line and read it */ 
    const Int32 MsDelayAfterLineBreakBeforeSayCommand = 150;

    /** Needed to insert this delay after the line break that is sent after a /say command, otherwise the ncomm script might not have enough time before starting the next command */
    const Int32 MsDelayAfterLineBreakAfterSayCommand = 1000;

    /** when false, the /say command will not make the Amiga speak ever */
    const bool bEnableSayCommand = true;

    /** Used to disable the /say command at runtime */
    bool bDisableSayCommand = false;

    // ANSI Escape codes:

    const string EscSeq_Reset = "\e[\0m";
    const string EscSeq_Red = "\e[\031m";
    const string EscSeq_Red_BG = "\e[\041m";
    const string EscSeq_Green = "\e[\032m";
    const string EscSeq_Green_BG = "\e[\042m";
    const string EscSeq_Yellow = "\e[\033m";
    const string EscSeq_Yellow_BG = "\e[\043m";
    const string EscSeq_Blue = "\e[\034m";
    const string EscSeq_Blue_BG = "\e[\044m";
    const string EscSeq_Magenta = "\e[\035m";
    const string EscSeq_Magenta_BG = "\e[\045m";
    const string EscSeq_Cyan = "\e[\036m";
    const string EscSeq_Cyan_BG = "\e[\046m";
    const string EscSeq_White = "\e[\037m";
    const string EscSeq_White_BG = "\e[\047m";
    const string EscSeq_Beep = "\a";

    /** Used to make the author name on each alternating message a different color */
    bool bAlternateAuthorColor = false;

    /** The display name of the author of the last youtube chat message we sent to the serial port */
    string LastAuthorDisplayName = "";

    /** These are commands that are read from inside chat messages, that are replaced and converted to other text (e.g. ANSI colors) */
    Dictionary<string, string> MessageCommands;

    static void Main(string[] args)
    {
        try
        {
            RunStartupTests();
            new YouTubeChatAmigaLink().Run().Wait();
        }
        catch (AggregateException ex)
        {
            foreach (var e in ex.InnerExceptions)
            {
                Console.WriteLine("ERROR: " + e.Message);
            }
        }
    }

    private async Task Run()
    {

        string ProgramName = "YouTube Chat Amiga Link (via Serial) " + ProgramVersion;
        Console.WriteLine(ProgramName);

        // Setup static data
        MessageCommands = new Dictionary<string, string>
        {
            // Reset colors:
            {"/reset",     EscSeq_Reset},

            // Foreground colors:          
            {"/blue",      EscSeq_Blue},
            {"/red",       EscSeq_Red},
            {"/cyan",      EscSeq_Cyan},
            {"/white",     EscSeq_White},
            {"/yellow",    EscSeq_Yellow},
            {"/magenta",   EscSeq_Magenta},
            {"/green",     EscSeq_Green },

            // Foreground colors (synonyms):   
            {"/b",      EscSeq_Blue},
            {"/r",       EscSeq_Red},
            {"/c",      EscSeq_Cyan},
            {"/w",     EscSeq_White},
            {"/y",    EscSeq_Yellow},
            {"/m",   EscSeq_Magenta},
            {"/g",     EscSeq_Green },

            // Background colors:
            {"/bgblue",    EscSeq_Blue_BG },
            {"/bgred",     EscSeq_Red_BG },
            {"/bggreen",   EscSeq_Green_BG },
            {"/bgcyan",    EscSeq_Cyan_BG },
            {"/bgyellow",  EscSeq_Yellow_BG },
            {"/bgmagenta", EscSeq_Magenta_BG },
            {"/bgwhite",   EscSeq_White_BG },

             // Background colors (synonyms):
            {"/bgb",    EscSeq_Blue_BG },
            {"/bgr",     EscSeq_Red_BG },
            {"/bgg",   EscSeq_Green_BG },
            {"/bgc",    EscSeq_Cyan_BG },
            {"/bgy",  EscSeq_Yellow_BG },
            {"/bgm", EscSeq_Magenta_BG },
            {"/bgw",   EscSeq_White_BG },

            // Other:
            {"/beep",      EscSeq_Beep}
        };

        // Making the assuption you only have 1 working com port, try all of these in order
        string[] ComPorts = { "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9" };

        // Connect to the Amiga serial port first, try all the comports on this machine      
        foreach (string ComPort in ComPorts)
        {
            try
            {
                AmigaSerialPort = OpenSerialPort(ComPort);
                if (AmigaSerialPort.IsOpen)
                {
                    string HelloMessage = EscSeq_Green + AmigaNewLine + "Hello from " + ProgramName + EscSeq_Reset;
                    WriteMessageToSerialPort(HelloMessage);
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to open Com Port: {ex.Message}");
            }
        }


        try
        {
            // Youtube API requires OAuth 2.0 authentication
            const string EnvVarSecret = "YouTubeAmigaClientSecret";
            const string EnvVarClientId = "YouTubeAmigaClientId";
            string? AppSecret = Environment.GetEnvironmentVariable(EnvVarSecret);
            if (AppSecret == null)
            {
                throw new Exception("Secret not defined: " + EnvVarSecret);
            }

            string? AppId = Environment.GetEnvironmentVariable(EnvVarClientId);
            if (AppSecret == null)
            {
                throw new Exception("App Id not defined: " + EnvVarClientId);
            }

            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                new ClientSecrets
                {
                    ClientId = AppId,
                    ClientSecret = AppSecret,
                },
                new[] { YouTubeService.Scope.YoutubeReadonly },
                "user3",    //TODO(davepoo2): this user can get a token that exires? should be a parameter?
                System.Threading.CancellationToken.None
            );

            // Create the YouTube service.
            var YTService = new YouTubeService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "YoutubeAmigaLiveChat",
            });

            Console.WriteLine("Executing a liveBroadcast request...");
            //The part parameter specifies a comma-separated list of one or more liveBroadcast resource properties that the API response will include.
            //The part names that you can include in the parameter value are id, snippet, contentDetails, monetizationDetails, and status.
            string Part = "snippet,contentDetails,status";
            LiveBroadcastsResource.ListRequest BroadcastRequest = YTService.LiveBroadcasts.List(Part);   //Returns a list of YouTube broadcasts that match the API request parameters.
            BroadcastRequest.Mine = true;
            LiveBroadcastListResponse response = await BroadcastRequest.ExecuteAsync();         // Execute request

            foreach (var liveBroadcastItem in response.Items)
            {
                string LiveBroadcastName = liveBroadcastItem.Snippet.Title.ToString();
                Console.WriteLine("LiveBroadcastId Found:" + liveBroadcastItem.Id + "     " + LiveBroadcastName);
                Console.WriteLine("-----------------");
                Console.WriteLine(liveBroadcastItem.Snippet.Description);
                Console.WriteLine("-----------------");
                WriteMessageToSerialPort(EscSeq_Cyan + LiveBroadcastName + EscSeq_Reset);

                // Run the request to get chat messages
                Console.WriteLine("Press ESC to exit");
                Console.WriteLine("\nGetting LiveChatMessages for: " + liveBroadcastItem.Snippet.LiveChatId.ToString());
                string NextPageToken = null;    //The first page token request of the app needs to be null, the rest are specified from the result returned from the API
                bDisableSayCommand = true;      //Disable the say command whilst getting the first page  

                do
                {
                    //The liveChatId parameter specifies the ID of the chat whose messages will be returned.
                    //The live chat ID associated with a broadcast is returned in the liveBroadcast resource's snippet.liveChatId property.
                    string MessagesPart = "id,snippet,authorDetails";
                    LiveChatMessagesResource.ListRequest MessagesRequest = YTService.LiveChatMessages.List(liveBroadcastItem.Snippet.LiveChatId, MessagesPart);
                    MessagesRequest.MaxResults = MaxYoutubeChatMessagesInOneBatch;
                    MessagesRequest.PageToken = NextPageToken;
                    LiveChatMessageListResponse MessagesResult = await MessagesRequest.ExecuteAsync();
                    TimeSpan WaitTimeForNextPoll = new TimeSpan(0, 0, 0, 0, (int)MessagesResult.PollingIntervalMillis.Value);
                    DateTime UtcBeforeSerialWork = DateTime.UtcNow;

                    // Display the results/chat messages
                    if (MessagesResult.Items != null)
                    {
                        bool bListCommands = false;
                        foreach (LiveChatMessage? Message in MessagesResult.Items)
                        {
                            // Author Name colors                            
                            if (LastAuthorDisplayName != Message.AuthorDetails.DisplayName)
                            {
                                // only change the color of the next author message if it was different to the last one
                                bAlternateAuthorColor = !bAlternateAuthorColor;                                
                            }
                            LastAuthorDisplayName = Message.AuthorDetails.DisplayName;
                            string NameColorEscapeCode = bAlternateAuthorColor ? EscSeq_Cyan : EscSeq_Yellow;

                            // Message color etc commands
                            string MessageText = Message.Snippet.TextMessageDetails.MessageText;
                            bListCommands = ReplaceCommandsInMessage(ref MessageText);

                            // Format the message and convert it to plain text that the Amiga can read
                            string DisplayMessageFormattedPC = NameColorEscapeCode + Message.AuthorDetails.DisplayName + ": " + EscSeq_Reset + MessageText + EscSeq_Reset;
                            WriteMessageToSerialPort(DisplayMessageFormattedPC);

                            // Process the command to give the ncomm /say script, it needs to be on it's own line for the ncomm script so the ncomm script can pick it up.
                            if (bEnableSayCommand && bDisableSayCommand == false && MessageText.Contains(SayCommand))
                            {
                                //everything after /say needs to be printed on it's own line and start with "/say", and add the author name. It needs to start with /say and end with \n\r          
                                string[] SplitMessage = Message.Snippet.TextMessageDetails.MessageText.Split(SayCommand, StringSplitOptions.None);
                                if (SplitMessage.Length == 2)
                                {
                                    string PrefixAuthorSays = FormatAuthorNameForSayCommand( Message.AuthorDetails.DisplayName ) + " says.";

                                    /** After the say command has been turned into ASCII and emojis enocoded as text, santise it further by taking out characters that
                                     * either break say command or can be used to do a CLI injection to get chat message to run commands in the Amiga CLI.
                                     * Doing this AFTER the ASCII conversion as many characters can all get converted into others, e.g. "- – —" all get converted to a "-". */
                                    Func<byte[], byte[]> SanitiseSayAfterEncoding = (byte[] InputAfterEncoding) =>
                                    {
                                        Encoding ASCIIEncoding = GetASCIIEncoding();
                                        string InputAsString = ASCIIEncoding.GetString(InputAfterEncoding);

                                        InputAsString = InputAsString.Replace('|', ' ');  //can be used to pipe to other commands e.g. dir | say  
                                        InputAsString = InputAsString.Replace("-x", "");  //-x is say command to read a filename
                                        InputAsString = InputAsString.Replace('#', ' ');  //Amiga cant say hashes
                                        InputAsString = InputAsString.Replace('€', ' ');  //Cause problems on cli say  
                                        InputAsString = InputAsString.Replace('<', ' ');  //CLI injection
                                        InputAsString = InputAsString.Replace('>', ' ');  //CLI injection
                                        InputAsString = InputAsString.Replace('`', '"');  //CLI injection
                                        InputAsString = InputAsString.Replace('\n', ' ');  //YT chat can't have breaks but remove anyway in case they are used to execute CLI commands on the Amiga on a new line
                                        InputAsString = InputAsString.Replace('\r', ' ');  //YT chat can't have breaks but remove anyway in case they are used to execute CLI commands on the Amiga one a new line
                                        // Allow "-" because this can be used by the chat message author to issue paramters to say such as -r for robot voice or -p100 for pitch 100
                                        
                                        // Find all Amiga dos commands that might do harm, and don't allow the message if it has one
                                        if (ContainsAmigaCLICommand(InputAsString))
                                        {
                                            InputAsString = SayCommand + " " + PrefixAuthorSays + " Message Redacted due to Amiga DOS command";
                                        }

                                        InputAsString += AmigaNewLine;      // The ncomm script waits for a line break before processing the command, so always add one or it won't say until the next message
                                        return ASCIIEncoding.GetBytes(InputAsString);
                                    };
                                    
                                    string SanitisedMessageToSay = SplitMessage[1].Trim();
                                    RemoveCommandsInMessage(ref SanitisedMessageToSay);
                                    DisplayMessageFormattedPC = SayCommand + " " + PrefixAuthorSays + " " + SanitisedMessageToSay;  
                                    WriteMessageToSerialPort(DisplayMessageFormattedPC, SanitiseSayAfterEncoding);
                                }
                            }
                        }

                        // Setup the next page
                        NextPageToken = MessagesResult.NextPageToken;
                        bDisableSayCommand = false;

                        // Show the commands once if any message requested it
                        if (bListCommands)
                        {
                            ListCommandsToSerialPort();
                        }
                    }

                    DateTime UtcAfterSerialWork = DateTime.UtcNow;
                    TimeSpan TimeTakeToDoSerialWork = UtcAfterSerialWork - UtcBeforeSerialWork;
                    WaitTimeForNextPoll -= TimeTakeToDoSerialWork;

                    // Wait before getting more messages                  
                    if (WaitTimeForNextPoll < MinWaitBetweenNextPageYouTubeMessages)
                    {
                        WaitTimeForNextPoll = MinWaitBetweenNextPageYouTubeMessages;
                    }

                    // Check Escape to exit
                    if (Console.KeyAvailable)
                    {
                        ConsoleKeyInfo KeyPress = Console.ReadKey(true);
                        if (KeyPress.Key == ConsoleKey.Escape)
                        {
                            break;
                        }
                    }

                    // Sleep
                    if (bShowWaitingMessage)
                    {
                        Console.WriteLine("Waiting for " + WaitTimeForNextPoll.TotalMilliseconds + "ms");
                    }
                    Thread.Sleep((int)WaitTimeForNextPoll.TotalMilliseconds);
                }
                while (true);
                break;
            }

        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }

        if (AmigaSerialPort.IsOpen)
        {
            Console.WriteLine("Closing serial port connection");
            AmigaSerialPort.Close();
        }

        Console.WriteLine("Exiting Application");
    }

    /** Find all the commands in the message and replace them with the ANSI escape codes 
     * @return true if a List commands command was found in the message text */
    private bool ReplaceCommandsInMessage(ref string MessageText)
    {
        bool bListCommands = false;
        if (MessageText.Contains("/"))
        {
            foreach (var MessageCommand in MessageCommands)
            {
                // Replace the command if it had a space after it, removing the space as well.
                MessageText = MessageText.Replace(MessageCommand.Key + " ", MessageCommand.Value);

                // Youtube trims the space on the end of messages so accept the command without the space, if it was the last thing on the line
                if (MessageText.EndsWith(MessageCommand.Key))
                {
                    MessageText = MessageText.Replace(MessageCommand.Key, MessageCommand.Value);
                }
            }

            if (MessageText.Contains(ListCommandsCommand))
            {
                bListCommands = true;
            }            
        }

        return bListCommands;
    }

    /** Find all the commands in the message and remove them */
    private void RemoveCommandsInMessage(ref string MessageText)
    {
        if (MessageText.Contains("/"))
        {
            foreach (var MessageCommand in MessageCommands)
            {
                MessageText = MessageText.Replace(MessageCommand.Key, "");
            }

            MessageText = MessageText.Replace(ListCommandsCommand, "");
            MessageText = MessageText.Replace(SayCommand, "");
        }
    }

    /** Sends the text to show all the commands to the serial port */
    private void ListCommandsToSerialPort()
    {
        WriteMessageToSerialPort("Commands Available: (all commands requires a space after them)");
        int LineLength = 0;
        string allcommands = ListCommandsCommand + " " + SayCommand + " ";
        foreach (var MessageCommand in MessageCommands)
        {
            string ThisCommand = MessageCommand.Key + " ";
            LineLength += ThisCommand.Length;
            allcommands = allcommands + ThisCommand;
        }

        string AmigaSayHelpText = @"
        /say Command options:
        -m      - Specifies a male voice.
        -f      - Specifies a female voice.
        -r      - Robot or monotone mode.
        -n      - Natural or standard speech mode.
        -s <n>  - Sets the speaking rate (speed in words per minute, typically from 40 to 400; default is around 150).
        -p <n>  - Sets the voice pitch (typically ranging from 65 to 320 Hz).";
        allcommands += AmigaSayHelpText;

        WriteMessageToSerialPort(allcommands);
    }

    /** Open an Amiga compatible serial port connection */
    private SerialPort OpenSerialPort(string ComPort = "COM3", int BaudRate = 9600, int DataBits = 8, StopBits _StopBits = StopBits.One)
    {
        SerialPort NewSerialPort;
        if (bDisableSerialLink == false)
        {
            Parity _Parity = Parity.None;
            Console.WriteLine("Opening serial port to: " + ComPort + ", Baud: " + BaudRate + ", Parity: " + _Parity + ", DataBits:" + DataBits + ", StopBits:" + _StopBits);
            // Create Port
            NewSerialPort = new SerialPort(ComPort, BaudRate, _Parity, DataBits, _StopBits);
            NewSerialPort.Open();

            // open the reader
            NewSerialPort.DataReceived += new SerialDataReceivedEventHandler(ReadMessageSerialPort);
        }
        else
        {
            NewSerialPort = new SerialPort();
            Console.WriteLine("Serial Connection is disabled.");
        }

        return NewSerialPort;
    }

    /** Read incoming data that from the serial port */
    private void ReadMessageSerialPort(object sender, SerialDataReceivedEventArgs e)
    {
        string NewData = AmigaSerialPort.ReadExisting();
        if (NewData.Contains(IncomingDataNewLine))
        {
            bool bListCommands = false;
            string[] Lines = IncommingDataFromSerialPort.Split(IncomingDataNewLine);
            foreach (string Line in Lines)
            {
                string FormattedLine = Line;
                bool bHasListCommands = ReplaceCommandsInMessage(ref FormattedLine);
                WriteMessageToSerialPort(FormattedLine + EscSeq_Reset);
                if (bHasListCommands)
                {
                    bListCommands = true;
                }
            }

            if (bListCommands)
            {
                ListCommandsToSerialPort();
            }

            if (Lines.Length > 0)
            {
                // All data has been processed, so clear it.
                IncommingDataFromSerialPort = "";
            }
        }
        else
        {
            IncommingDataFromSerialPort += NewData;
        }
    }

    /** @return the encoding to turn strings in ASCII that Ncomm can read */
    private Encoding GetASCIIEncoding()
    {
        Encoding Encoding = System.Text.Encoding.GetEncoding(28591); // extended ASCII
        return Encoding;
    }

    /* No-operation of byte[] string santisier
     * @return Default unchanged */
    private byte[] DefaultStringSanitiser( byte[] Default )
    {
        return Default;
    }

    /** Takes a message in a string native to this machine and encodes it so the Amiga can read it, then sends it over the serial link. 
     * This will block so not to overflow the buffers on the target machine.
     * Also write the message the console on the this machine.
     * @param Message The message to be sent
     * @param AmigaSerialPort The serial port to send the message on. If the port isn't open, then sending the message will be skipped silently */
    private void WriteMessageToSerialPort(string Message)
    {
        WriteMessageToSerialPort(Message, DefaultStringSanitiser);
    }

    /** Takes a message in a string native to this machine and encodes it so the Amiga can read it, then sends it over the serial link. 
     * This will block so not to overflow the buffers on the target machine.
     * Also write the message the console on the this machine.
     * @param Message The message to be sent
     * @param AmigaSerialPort The serial port to send the message on. If the port isn't open, then sending the message will be skipped silently #
     * param CleanupMethod Function to be called after conversion to ASCII so the string can be santised before sending via the serial port*/
    private void WriteMessageToSerialPort( string Message, Func<byte[], byte[]> CleanupMethod)
    {
        Message = Message.ReplaceLineEndings(AmigaNewLine);
        Message = EmojiConverter.ToText(Message);   // Convert emojis to text for the amiga
        Encoding Encoding = GetASCIIEncoding();
        byte[] DisplayMessageAmiga = Encoding.GetBytes(Message);
        DisplayMessageAmiga= CleanupMethod(DisplayMessageAmiga);
        Console.WriteLine(Encoding.GetString(DisplayMessageAmiga));

        if (AmigaSerialPort.IsOpen)
        {
            // Write the message but split into lines so Ncomm can display it, and don't send messages too fast to overflow buffers on the target machine.
            AmigaSerialPort.Write(AmigaNewLine);
            if (Message.StartsWith(SayCommand))
            {
                Thread.Sleep(MsDelayAfterLineBreakBeforeSayCommand);   // Wait, or the ncomm script can't find the /say command
            }
            for (int Offset = 0; Offset < DisplayMessageAmiga.Count();)
            {
                if ( Offset > 0 && bAddLineWraps)
                {
                    AmigaSerialPort.Write(AmigaNewLine);
                }

                int NumCharsLeftToDisplay = DisplayMessageAmiga.Count() - Offset;
                int NumCharsToSend = Math.Min(NumCharsLeftToDisplay, MaxCharsPerLine);
                AmigaSerialPort.Write(DisplayMessageAmiga, Offset, NumCharsToSend);
                Offset += NumCharsToSend;

                Thread.Sleep(MsSleepBetweenSerialMessageSend);   // Wait before sending next bufffer part
            }

            if (Message.StartsWith(SayCommand))
            {
                Thread.Sleep(MsDelayAfterLineBreakAfterSayCommand);   // Wait, or the ncomm script might not process the next /say command
            }
        }
    }

    // Dangerous Amiga CLI commands that can cause permanent changes
    static readonly string[] DangerousAmigaCommands = new string[]
    {
        "DELETE", "FORMAT", "DISKCOPY", "RENAME", "PROTECT", "RELABEL",
        "ASSIGN", "SETENV", "SET", "RESIDENT", "UNMOUNT", "EJECT",
        "FAIL", "QUIT", "EXIT"
    };

    /** Checks if the input string contains any Amiga CLI commands that could cause harm to the Amiga */
    static bool ContainsAmigaCLICommand(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        // Split into words and check for any dangerous command
        return input
            .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(word => DangerousAmigaCommands.Contains(word.ToUpperInvariant()));
    }

    /** Given the author name from YouTube, format it so the Amiga say command can read it better */
    static string FormatAuthorNameForSayCommand(string AuthorName)
    {
        string FormattedName = AuthorName.TrimStart('@');

        // Regex: [A-Z] matches any uppercase letter
        FormattedName = Regex.Replace(FormattedName, "[A-Z]", match =>
        {
            return " " + match.Value[0]; // add space before capitals, because say doesn't treat capitalised words as a new word unless they are space separated.
        });

        // prefix spaces before and after strings of numbers e.g, "Foo42Bar becomes "Foo 42 Bar"
        string FormattedNumbers = "";
        bool bFoundNumber = false;
        foreach (char c in FormattedName)
        {
            string Prefix = c == ' ' ? "" : " ";    //don't double space
            bool bIsNumber = c >= '0' && c <= '9';
            if (bIsNumber != bFoundNumber)
            {
                FormattedNumbers += Prefix + c;
                bFoundNumber = !bFoundNumber;
            }
            else
            {
                FormattedNumbers += c;
            }
        }
        FormattedName = FormattedNumbers.Trim();
        return FormattedName;
    }

#if DEBUG
    /** Some basic tests to see if we can find amiga commands */
    static void TestAmigaCommandFind()
    {
        string testString1 = "DELETE file.txt FORMAT DF0: COPY safe.txt TO RAM:";
        if (ContainsAmigaCLICommand(testString1) != true)
        {
            throw new Exception("Failed Amiga CLI command test 1");
        }

        string testString2 = "delete file.txt format DF0:";
        if (ContainsAmigaCLICommand(testString2) != true)
        {
            throw new Exception("Failed Amiga CLI command test 2");
        }

        string testString3 = "COPY safe.txt TO RAM:";
        if (ContainsAmigaCLICommand(testString3) != false)
        {
            throw new Exception("Failed Amiga CLI command test 3");
        }
    }

    /** Some basic tests that we can format an author name for say */
    static void TestAuthorNameFormattingForSayCommand()
    {
        string ErrorString = "Failed FormatAuthorNameForSayCommand for test ";

        if (FormatAuthorNameForSayCommand("DavePoo2") != "Dave Poo 2")
        {
            throw new Exception(ErrorString + "1");
        }

        if (FormatAuthorNameForSayCommand("DavePoo2dave") != "Dave Poo 2 dave")
        {
            throw new Exception(ErrorString + "2");
        }

        if (FormatAuthorNameForSayCommand("2000davePoo2") != "2000 dave Poo 2")
        {
            throw new Exception(ErrorString + "3");
        }

        if (FormatAuthorNameForSayCommand("2000DavePoo") != "2000 Dave Poo")
        {
            throw new Exception(ErrorString + "4");
        }

        if (FormatAuthorNameForSayCommand("@DavePoo2") != "Dave Poo 2")
        {
            throw new Exception(ErrorString + "5");
        }

        if (FormatAuthorNameForSayCommand("@DavePoo12345") != "Dave Poo 12345")
        {
            throw new Exception(ErrorString + "6");
        }
    }
#endif

    // Run any tests before the main program starts, thow exceptions if anything fails
    static void RunStartupTests()
    {
#if DEBUG
        TestAmigaCommandFind();
        TestAuthorNameFormattingForSayCommand();
#endif
    }
}