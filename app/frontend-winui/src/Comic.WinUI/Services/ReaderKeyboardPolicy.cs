using Windows.System;

namespace Comic.WinUI.Services;

internal enum ReaderKeyAction { None, Previous, Next, First, Last, Fullscreen, Back }

internal static class ReaderKeyboardPolicy
{
    internal static ReaderKeyAction Resolve(VirtualKey key, ReaderShortcutPreference shortcuts,
        bool strip, bool fullscreen, bool inputOwnsKey, bool modified)
    {
        if (inputOwnsKey || modified) return ReaderKeyAction.None;
        if (string.Equals(key.ToString(), shortcuts.FullscreenKey, StringComparison.OrdinalIgnoreCase)) return ReaderKeyAction.Fullscreen;
        if (string.Equals(key.ToString(), shortcuts.PreviousKey, StringComparison.OrdinalIgnoreCase)) return ReaderKeyAction.Previous;
        if (string.Equals(key.ToString(), shortcuts.NextKey, StringComparison.OrdinalIgnoreCase)) return ReaderKeyAction.Next;
        return key switch
        {
            VirtualKey.Left => ReaderKeyAction.Previous,
            VirtualKey.Right => ReaderKeyAction.Next,
            VirtualKey.PageUp when !strip => ReaderKeyAction.Previous,
            VirtualKey.PageDown when !strip => ReaderKeyAction.Next,
            VirtualKey.Home when !strip => ReaderKeyAction.First,
            VirtualKey.End when !strip => ReaderKeyAction.Last,
            VirtualKey.Escape => fullscreen ? ReaderKeyAction.Fullscreen : ReaderKeyAction.Back,
            _ => ReaderKeyAction.None
        };
    }
}
