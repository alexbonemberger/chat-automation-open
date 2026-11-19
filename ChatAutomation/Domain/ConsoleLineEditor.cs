using System.Text;
using System.Runtime.InteropServices;

namespace ChatAutomation.Domain;

// Editor leve para o console Windows. Nenhuma entrada executa comandos antes
// do Enter; TAB apenas substitui o texto em memoria.
public sealed class ConsoleLineEditor
{
    private readonly List<string> _history = [];

    public string? ReadLine(string prompt, Func<IReadOnlyList<string>> getChatNames,
        Func<IReadOnlyList<string>>? getProjectIds = null)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Write(prompt);
            return Console.ReadLine(); // Compatibilidade com pipes e logs.
        }

        var text = new StringBuilder();
        var cursor = 0;
        var cycle = new CompletionCycle();
        var historyIndex = _history.Count;
        var originalDraft = "";
        var top = Console.CursorTop;
        Render(prompt, text.ToString(), cursor, top);

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                var result = text.ToString();
                if (!string.IsNullOrWhiteSpace(result) &&
                    (_history.Count == 0 || _history[^1] != result))
                    _history.Add(result);
                return result;
            }

            // AltGr em teclados internacionais costuma chegar como Ctrl+Alt
            // com KeyChar imprimível. Colar via Ctrl+V/Shift+Insert também
            // deve inserir o texto literal, sem executar Enter do clipboard.
            if (IsPasteShortcut(key))
            {
                var pasted = WindowsClipboardText.TryRead();
                if (!string.IsNullOrEmpty(pasted))
                {
                    var fragment = FlattenPaste(pasted);
                    text.Insert(cursor, fragment);
                    cursor += fragment.Length;
                    cycle.Reset();
                }
                Render(prompt, text.ToString(), cursor, top);
                continue;
            }

            if (key.Key == ConsoleKey.Tab)
            {
                var item = cycle.Next(text.ToString(), getChatNames(),
                    backwards: key.Modifiers.HasFlag(ConsoleModifiers.Shift),
                    projectIds: getProjectIds?.Invoke());
                if (item is not null)
                {
                    text.Clear().Append(item.Text);
                    cursor = item.Cursor;
                }
                Render(prompt, text.ToString(), cursor, top);
                continue;
            }

            cycle.Reset();
            switch (key.Key)
            {
                case ConsoleKey.LeftArrow:
                    if (cursor > 0) cursor--;
                    break;
                case ConsoleKey.RightArrow:
                    if (cursor < text.Length) cursor++;
                    break;
                case ConsoleKey.Home:
                    cursor = 0;
                    break;
                case ConsoleKey.End:
                    cursor = text.Length;
                    break;
                case ConsoleKey.Backspace:
                    if (cursor > 0)
                    {
                        text.Remove(cursor - 1, 1);
                        cursor--;
                    }
                    break;
                case ConsoleKey.Delete:
                    if (cursor < text.Length) text.Remove(cursor, 1);
                    break;
                case ConsoleKey.Escape:
                    text.Clear();
                    cursor = 0;
                    historyIndex = _history.Count;
                    break;
                case ConsoleKey.UpArrow:
                    if (_history.Count == 0 || historyIndex == 0) break;
                    if (historyIndex == _history.Count) originalDraft = text.ToString();
                    historyIndex--;
                    text.Clear().Append(_history[historyIndex]);
                    cursor = text.Length;
                    break;
                case ConsoleKey.DownArrow:
                    if (historyIndex >= _history.Count) break;
                    historyIndex++;
                    text.Clear().Append(historyIndex == _history.Count
                        ? originalDraft : _history[historyIndex]);
                    cursor = text.Length;
                    break;
                default:
                    if (ShouldInsertPrintable(key))
                    {
                        text.Insert(cursor, key.KeyChar);
                        cursor++;
                    }
                    break;
            }

            Render(prompt, text.ToString(), cursor, top);
        }
    }

    internal static bool ShouldInsertPrintable(ConsoleKeyInfo key)
    {
        if (char.IsControl(key.KeyChar)) return false;
        var ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        var alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        // Ctrl+Alt equivale a AltGr em vários layouts, incluindo variantes
        // ABNT2. Nunca perder o caractere produzido apenas pelos modificadores.
        // Ctrl isolado e Alt isolado continuam reservados para atalhos.
        return ctrl == alt;
    }

    internal static bool IsPasteShortcut(ConsoleKeyInfo key) =>
        (key.Key == ConsoleKey.V &&
         key.Modifiers.HasFlag(ConsoleModifiers.Control) &&
         !key.Modifiers.HasFlag(ConsoleModifiers.Alt)) ||
        (key.Key == ConsoleKey.Insert &&
         key.Modifiers.HasFlag(ConsoleModifiers.Shift) &&
         !key.Modifiers.HasFlag(ConsoleModifiers.Control) &&
         !key.Modifiers.HasFlag(ConsoleModifiers.Alt));

    internal static string FlattenPaste(string pasted) =>
        pasted.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ')
            .Replace("\0", "");

    // Uma unica linha com rolamento horizontal para comandos/URLs longos.
    // Nao cria novas linhas acidentalmente ao chegar na borda do CMD.
    private static void Render(string prompt, string line, int cursor, int top)
    {
        var width = Math.Max(12, Console.WindowWidth);
        var displayPrompt = prompt.Length <= width - 3
            ? prompt : prompt[..(width - 3)];
        var room = Math.Max(1, width - displayPrompt.Length - 1);
        var scroll = Math.Clamp(cursor - room + 1, 0, Math.Max(0, line.Length - room));
        var count = Math.Min(room, Math.Max(0, line.Length - scroll));
        var visible = line.Substring(scroll, count);
        var rendered = (displayPrompt + visible).PadRight(width - 1);

        Console.SetCursorPosition(0, top);
        Console.Write(rendered);
        Console.SetCursorPosition(
            Math.Clamp(displayPrompt.Length + cursor - scroll, 0, width - 2), top);
    }
}

internal static class WindowsClipboardText
{
    private const uint CfUnicodeText = 13;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    public static string? TryRead()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero) return null;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(pointer); }
            finally { _ = GlobalUnlock(handle); }
        }
        finally { _ = CloseClipboard(); }
    }
}
