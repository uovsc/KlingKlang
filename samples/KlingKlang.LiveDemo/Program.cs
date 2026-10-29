using KlingKlang;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: KlingKlang.LiveDemo <48-kHz-PCM16-WAV>");
    return 2;
}

AudioClip clip = AudioClip.LoadWav(args[0]);
AudioEngine audio = AudioEngine.Create();


try
{
    VoiceHandle voice = audio.Play(clip, loop: false);
    while(audio.IsPlaying(voice)) { 
        Thread.Sleep(100); 
    }
    voice = audio.Play(clip, loop: false);
    while(audio.IsPlaying(voice)) { 
        Thread.Sleep(100); 
    }
    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
    InvalidDataException or NotSupportedException or InvalidOperationException)
{
    Console.Error.WriteLine(exception);
    return 1;
}
finally
{
    audio.Dispose();
    clip.Dispose();
}


/*
        private AudioEngine? audio = null;
        private AudioClip? clip = null;


        public MainWindow()
        {
            InitializeComponent();

            SelectInput(InputMM);

            DataContextChanged += MainWindow_DataContextChanged;

            audio = AudioEngine.Create();
            clip = LoadClip($"/home/ufo/Dev/DevN/GodMorgon3/GodMorgon/Assets/alarm2.wav");     
        }

        private void TimerItemFinished(object? sender, System.EventArgs e) {
            try {
                Dispatcher.UIThread.Post(() =>
                {
                    VoiceHandle voice = audio.Play(clip, loop: false);
                    while(audio.IsPlaying(voice)) { 
                        Thread.Sleep(100); 
                    }
                });
            } 
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message + ex.InnerException.Message);
            }
        }*/