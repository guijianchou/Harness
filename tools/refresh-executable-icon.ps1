<#
.SYNOPSIS
Refresh the Windows shell icon for one existing executable after publishing it.
.EXAMPLE
./tools/refresh-executable-icon.ps1 -Path ./artifacts/openclaw-preview/win-x64/Harness.exe
.NOTES
Only notifies the shell about the executable and its containing directory.
Does not delete icon caches, restart Explorer, or launch the executable.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedPath = Resolve-Path -LiteralPath $Path -ErrorAction Stop
if ($resolvedPath.Provider.Name -ne 'FileSystem') {
    throw 'Path must identify an executable on the filesystem.'
}

$executable = Get-Item -LiteralPath $resolvedPath.ProviderPath -ErrorAction Stop
if ($executable -isnot [System.IO.FileInfo] -or $executable.Extension -ine '.exe') {
    throw 'Path must identify an existing .exe file.'
}

if (-not $PSCmdlet.ShouldProcess($executable.FullName, 'Refresh the executable icon and containing directory in the Windows shell')) {
    return
}

if (-not ('Harness.Tools.ExecutableIconRefresh' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace Harness.Tools
{
    public static class ExecutableIconRefresh
    {
        private const uint UpdateItem = 0x00002000;
        private const uint UpdateDirectory = 0x00001000;
        private const uint PathUnicode = 0x0005;
        private const uint Flush = 0x1000;

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

        public static void Refresh(string executablePath, string directoryPath)
        {
            Notify(UpdateItem, executablePath);
            Notify(UpdateDirectory, directoryPath);
        }

        private static void Notify(uint eventId, string path)
        {
            IntPtr unicodePath = Marshal.StringToHGlobalUni(path);
            try
            {
                // FLUSH returns after delivery, keeping the Unicode path valid for the call.
                SHChangeNotify(eventId, PathUnicode | Flush, unicodePath, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(unicodePath);
            }
        }
    }
}
'@
}

[Harness.Tools.ExecutableIconRefresh]::Refresh($executable.FullName, $executable.DirectoryName)
[pscustomobject]@{
    Executable = $executable.FullName
    Directory = $executable.DirectoryName
    Notifications = 'SHCNE_UPDATEITEM, SHCNE_UPDATEDIR'
}
