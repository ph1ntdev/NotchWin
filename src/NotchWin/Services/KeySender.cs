using System.Runtime.InteropServices;
using System.Windows.Input;

namespace NotchWin.Services;

/// <summary>Нажимает сочетания клавиш от имени пользователя (горячие клавиши Discord, Ctrl+C).</summary>
internal static class KeySender
{
    /// <summary>Сочетание вида «Ctrl+Shift+Alt+M».</summary>
    public static void Send(string combo)
    {
        var keys = new List<ushort>();
        foreach (var part in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            ushort vk = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => 0x11,
                "shift" => 0x10,
                "alt" => 0x12,
                "win" => 0x5B,
                _ => Enum.TryParse<Key>(part, true, out var key) ? (ushort)KeyInterop.VirtualKeyFromKey(key) : (ushort)0,
            };
            if (vk != 0) keys.Add(vk);
        }
        if (keys.Count == 0) return;
        var inputs = keys.Select(k => Make(k, false)).Concat(Enumerable.Reverse(keys).Select(k => Make(k, true))).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Ждёт, пока пользователь отпустит Ctrl, Alt, Shift и Win (после своей горячей клавиши).</summary>
    public static async Task WaitForModifiersReleasedAsync(int timeoutMs = 1000)
    {
        var until = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < until && new[] { 0x11, 0x10, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
            await Task.Delay(20);
    }

    // Discord и другие программы с глобальными сочетаниями смотрят и на скан-код клавиши, поэтому передаём оба.
    private static INPUT Make(ushort vk, bool up) => new()
    {
        type = 1,
        ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = up ? 2u : 0u },
    };

    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
        private readonly long _padding; // объединение в INPUT больше, чем KEYBDINPUT
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
