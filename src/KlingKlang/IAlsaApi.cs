namespace KlingKlang;

internal interface IAlsaApi
{
    int Open(out IntPtr handle);
    int SetParameters(IntPtr handle);
    int GetParameters(IntPtr handle, out nuint bufferFrames, out nuint periodFrames);
    nint Available(IntPtr handle);
    nint Write(IntPtr handle, IntPtr samples, nuint frames);
    int Prepare(IntPtr handle);
    int Resume(IntPtr handle);
    int Drop(IntPtr handle);
    int Close(IntPtr handle);
    string ErrorString(int error);
}
