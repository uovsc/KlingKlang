using KlingKlang;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: KlingKlang.LiveDemo <48-kHz-PCM16-WAV>");
    return 2;
}

try
{
    using var clip = AudioClip.LoadWav(args[0]);
    for (int i = 0; i < 3; i++)
    {
        using var audio = AudioEngine.Create();
        Console.WriteLine($"Playback {i + 1}: full volume.");
        VoiceHandle voice = audio.Play(clip, loop: true);
        Thread.Sleep(12000);

        for(float vol=1f; vol>=0f; vol-=.1f) {
            Console.WriteLine($"Volume: {Math.Round(vol * 100)}");
            if (!audio.SetVolume(voice, vol))
            {
                throw new InvalidOperationException("The voice stopped unexpectedly.");
            }
            Thread.Sleep(500);
        }

        if (!audio.Stop(voice))
        {
            throw new InvalidOperationException("The voice stopped unexpectedly.");
        }
        Thread.Sleep(200);
    }

    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
    InvalidDataException or NotSupportedException or InvalidOperationException)
{
    Console.Error.WriteLine(exception);
    return 1;
}
