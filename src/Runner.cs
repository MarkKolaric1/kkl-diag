// Runs kw1281test.exe inside a hidden console and lets the UI read that console's
// screen and send key presses to it. kw1281test uses real console features (cursor
// moves for live values, key presses to navigate), so plain output redirection is
// not enough for every command.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace KklDiag
{
    public class Runner
    {
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        static readonly IntPtr INVALID = new IntPtr(-1);
        static readonly object ConsoleLock = new object();

        [StructLayout(LayoutKind.Sequential)] struct COORD { public short X; public short Y; }
        [StructLayout(LayoutKind.Sequential)] struct SMALL_RECT { public short Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct CONSOLE_SCREEN_BUFFER_INFO { public COORD dwSize; public COORD dwCursorPosition; public short wAttributes; public SMALL_RECT srWindow; public COORD dwMaximumWindowSize; }
        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
        struct INPUT_RECORD
        {
            [FieldOffset(0)] public ushort EventType;
            [FieldOffset(4)] public int bKeyDown;
            [FieldOffset(8)] public ushort wRepeatCount;
            [FieldOffset(10)] public ushort wVirtualKeyCode;
            [FieldOffset(12)] public ushort wVirtualScanCode;
            [FieldOffset(14)] public char UnicodeChar;
            [FieldOffset(16)] public uint dwControlKeyState;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetConsoleScreenBufferInfo(IntPtr h, out CONSOLE_SCREEN_BUFFER_INFO info);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ReadConsoleOutputCharacterW(IntPtr h, [Out] char[] buf, uint len, COORD coord, out uint read);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool WriteConsoleInputW(IntPtr h, INPUT_RECORD[] recs, uint len, out uint written);

        public const string DoneMarker = "__KKL_DONE__";

        Process _process;
        public bool IsRunning { get { return _process != null && !_process.HasExited; } }
        public string LastScreen { get; private set; }

        public void Start(string exePath, string workDir, string args)
        {
            Stop();
            Directory.CreateDirectory(workDir);
            // cmd keeps the console alive after kw1281test exits, so the final output can still be read.
            var psi = new ProcessStartInfo("cmd.exe",
                "/s /c \" \"" + exePath + "\" " + args + " & echo " + DoneMarker + " & pause >nul \"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = workDir;
            LastScreen = "";
            _process = Process.Start(psi);
        }

        // Returns the full text of the console, or the last good read if it can't attach.
        public string ReadScreen()
        {
            if (_process == null) return LastScreen;
            lock (ConsoleLock)
            {
                if (_process.HasExited || !AttachConsole((uint)_process.Id)) return LastScreen;
                try
                {
                    IntPtr h = CreateFileW("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h == INVALID) return LastScreen;
                    try
                    {
                        CONSOLE_SCREEN_BUFFER_INFO info;
                        if (!GetConsoleScreenBufferInfo(h, out info)) return LastScreen;
                        int width = info.dwSize.X;
                        int rows = info.dwCursorPosition.Y + 1;
                        var buf = new char[width * rows];
                        uint read;
                        ReadConsoleOutputCharacterW(h, buf, (uint)buf.Length, new COORD(), out read);
                        var sb = new System.Text.StringBuilder();
                        for (int y = 0; y < rows; y++)
                            sb.Append(new string(buf, y * width, width).TrimEnd()).Append('\n');
                        LastScreen = sb.ToString();
                        return LastScreen;
                    }
                    finally { CloseHandle(h); }
                }
                finally { FreeConsole(); }
            }
        }

        public bool IsDone { get { return LastScreen != null && LastScreen.Contains(DoneMarker); } }

        // Sends one key press (virtual key code + character) to the hidden console.
        public void SendKey(ushort vk, char ch)
        {
            if (!IsRunning) return;
            lock (ConsoleLock)
            {
                if (!AttachConsole((uint)_process.Id)) return;
                try
                {
                    IntPtr h = CreateFileW("CONIN$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h == INVALID) return;
                    try
                    {
                        var recs = new INPUT_RECORD[2];
                        for (int i = 0; i < 2; i++)
                        {
                            recs[i].EventType = 1;
                            recs[i].bKeyDown = i == 0 ? 1 : 0;
                            recs[i].wRepeatCount = 1;
                            recs[i].wVirtualKeyCode = vk;
                            recs[i].UnicodeChar = ch;
                        }
                        uint written;
                        WriteConsoleInputW(h, recs, 2, out written);
                    }
                    finally { CloseHandle(h); }
                }
                finally { FreeConsole(); }
            }
        }

        public void Stop()
        {
            if (_process == null) return;
            try
            {
                if (!_process.HasExited)
                {
                    // Kill cmd and kw1281test together.
                    var kill = new ProcessStartInfo("taskkill", "/T /F /PID " + _process.Id);
                    kill.UseShellExecute = false; kill.CreateNoWindow = true;
                    using (var k = Process.Start(kill)) k.WaitForExit(3000);
                }
            }
            catch { }
            _process.Dispose();
            _process = null;
        }
    }
}
