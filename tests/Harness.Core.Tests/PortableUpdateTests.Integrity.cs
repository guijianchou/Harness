// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Harness.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

public sealed partial class PortableUpdateTests
{
    [TestMethod]
    public async Task PreparationNormalizesOnlyStagedAppHostsUnderLowParent()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows mandatory integrity labels are required."); }
        using var tree = new TempTree();
        SetTestIntegrity(tree.Root, "S:(ML;OICI;NW;;;LW)");
        var parentLabel = ReadSecurity(tree.Root, 0x10);
        var parentDacl = ReadSecurity(tree.Root, 4);
        var installation = MakePackage(Path.Combine(tree.Root, "installation"), "5.3.8");
        var package = MakePackage(Path.Combine(tree.Root, "download-source"), "5.10.0");
        var archive = Path.Combine(tree.Root, "download.zip");
        ZipFile.CreateFromDirectory(package, archive);
        var bytes = File.ReadAllBytes(archive);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var sourceDacl = ReadSecurity(Path.Combine(package, "Harness.exe"), 4);
        using var http = Http(request => request.RequestUri!.AbsolutePath.EndsWith(".sha256")
            ? Text(hash + "  Harness-5.10.0-win-x64.zip") : Bytes(bytes));

        var job = await PortableUpdatePreparation.PrepareAsync(new GitHubReleaseClient(http), DownloadRelease(bytes.Length),
            installation, "5.3.8", null, default);

        foreach (var relative in new[] { "payload/Harness.exe", "payload/Updater/Harness.Updater.exe", "runner/Harness.Updater.exe" })
        {
            var path = Path.Combine(job, relative);
            StringAssert.Contains(ReadSecurity(path, 0x10), ";;;ME)", relative);
            Assert.AreEqual(sourceDacl, ReadSecurity(path, 4), "Normalizing integrity must preserve inherited access permissions.");
        }
        StringAssert.Contains(ReadSecurity(Path.Combine(job, "payload", "Harness.dll"), 0x10), ";;;LW)");
        StringAssert.Contains(ReadSecurity(Path.Combine(installation, "Harness.exe"), 0x10), ";;;LW)", "The existing installation must remain unchanged.");
        Assert.AreEqual(parentLabel, ReadSecurity(tree.Root, 0x10));
        Assert.AreEqual(parentDacl, ReadSecurity(tree.Root, 4));
        await PortablePackage.VerifyAsync(Path.Combine(job, "payload"), "5.10.0", false, default);
    }

    private static void SetTestIntegrity(string path, string sddl)
    {
        if (!ConvertTestDescriptor(sddl, 1, out var descriptor, out _)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            if (!SetTestFileSecurity(path, 0x10, descriptor)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        }
        finally { FreeTestDescriptor(descriptor); }
    }

    private static string ReadSecurity(string path, uint information)
    {
        var error = GetTestSecurity(path, 1, information, out _, out _, out _, out _, out var descriptor);
        if (error != 0) { throw new Win32Exception((int)error); }
        try
        {
            if (!StringifyTestDescriptor(descriptor, 1, information, out var text, out _)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            try { return Marshal.PtrToStringUni(text)!; }
            finally { FreeTestDescriptor(text); }
        }
        finally { FreeTestDescriptor(descriptor); }
    }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertTestDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);

    [DllImport("advapi32.dll", EntryPoint = "SetFileSecurityW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTestFileSecurity(string path, uint securityInformation, IntPtr descriptor);

    [DllImport("advapi32.dll", EntryPoint = "GetNamedSecurityInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetTestSecurity(string path, uint type, uint information, out IntPtr owner, out IntPtr group,
        out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll", EntryPoint = "ConvertSecurityDescriptorToStringSecurityDescriptorW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StringifyTestDescriptor(IntPtr descriptor, uint revision, uint information, out IntPtr text, out uint length);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", ExactSpelling = true)]
    private static extern IntPtr FreeTestDescriptor(IntPtr memory);
}
