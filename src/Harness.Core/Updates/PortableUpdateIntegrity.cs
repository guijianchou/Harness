// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Harness.Updates;

public static class PortableUpdateIntegrity
{
    public static void NormalizePreparedAppHosts(string job)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        _ = PortableUpdatePaths.ReadPlan(job);
        string[] appHosts = ["payload/Harness.exe", "payload/Updater/Harness.Updater.exe", "runner/Harness.Updater.exe"];
        var paths = appHosts.Select(relative => PortablePackage.ResolvePath(job, relative)).ToArray();
        foreach (var path in paths)
        {
            PortablePackage.RejectReparsePoints(path);
            if (!File.Exists(path)) { throw new PortableUpdateException("InvalidPackage"); }
        }
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;;NW;;;ME)", 1, out var descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            foreach (var path in paths)
            {
                // LABEL_SECURITY_INFORMATION changes only the mandatory label, never the DACL.
                // New files can inherit Low from the installation's parent after ZIP extraction.
                if (!SetFileSecurity(path, 0x00000010, descriptor))
                {
                    throw new UnauthorizedAccessException("The staged update executable's integrity label could not be prepared.",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                }
            }
        }
        finally { LocalFree(descriptor); }
    }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);

    [DllImport("advapi32.dll", EntryPoint = "SetFileSecurityW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileSecurity(string path, uint securityInformation, IntPtr descriptor);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
