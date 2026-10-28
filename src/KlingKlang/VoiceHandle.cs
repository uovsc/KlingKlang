namespace KlingKlang;

public readonly struct VoiceHandle
{
    internal VoiceHandle(object owner, int slot, long generation)
    {
        Owner = owner;
        Slot = slot;
        Generation = generation;
    }

    internal object? Owner { get; }
    internal int Slot { get; }
    internal long Generation { get; }
}
