using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class ConfigureInterop
{
    [DllImport("Configure.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVs1Count();

    [DllImport("Configure.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr GetVs1Name(int index);

    public static List<string> GetVs1Names()
    {
        int count = GetVs1Count();
        var names = new List<string>();
        for (int i = 0; i < count; i++)
        {
            IntPtr ptr = GetVs1Name(i);
            names.Add(Marshal.PtrToStringAnsi(ptr));
        }
        return names;
    }
    [DllImport("Configure.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVs2Count();

    [DllImport("Configure.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr GetVs2Name(int index);

    public static List<string> GetVs2Names()
    {
        int count = GetVs2Count();
        var names = new List<string>();
        for (int i = 0; i < count; i++)
        {
            IntPtr ptr = GetVs2Name(i);
            names.Add(Marshal.PtrToStringAnsi(ptr));
        }
        return names;
    }


}
