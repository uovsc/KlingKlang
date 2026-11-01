using KlingKlang;


if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: KlingKlang.LiveDemo <8-48-kHz-PCM16-WAV>");
    return 2;
}

AudioClip clip = AudioClip.LoadWav(args[0]);
AudioEngine audio = AudioEngine.Create();
bool isFullPlay = false;
try
{
    if(isFullPlay)
        PlayFull();
    else {
        PlayFading(10000, 2000);
        PlayFading(5000, 1000);
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

void PlayFull()
{
    VoiceHandle voice = audio.Play(clip, loop: false);
    while(audio.IsPlaying(voice)) { 
        Thread.Sleep(100); 
    }
}

void PlayFading(int fullDuration, int fadeDuration, float steps=10f)
{
    VoiceHandle voice = audio.Play(clip, loop: false);
    Thread.Sleep(fullDuration);

    for(float volume = 1.0f; volume >= 0.0f; volume -= 1.0f/steps)
    {
        audio.SetVolume(voice, volume);
        Console.WriteLine($"Volume: {Math.Round(volume * 100)}%");
        Thread.Sleep(fadeDuration/(int)steps);
    }
}
