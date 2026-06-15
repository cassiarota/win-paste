using System;
using System.Collections.Generic;
using PasteNowWin.Interop;

namespace PasteNowWin.Services;

/// <summary>Registers system-wide hotkeys and raises an event when one fires.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly NativeMessageWindow _msg;
    private readonly List<int> _registered = new();

    /// <summary>Raised with the hotkey id passed to <see cref="Register"/>.</summary>
    public event Action<int>? HotkeyPressed;

    public HotkeyManager(NativeMessageWindow msg)
    {
        _msg = msg;
        _msg.MessageReceived += OnMessage;
    }

    /// <summary>Returns false if the combination is already taken by another app.</summary>
    public bool Register(int id, uint modifiers, uint virtualKey)
    {
        bool ok = NativeMethods.RegisterHotKey(_msg.Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey);
        if (ok)
        {
            _registered.Add(id);
        }
        return ok;
    }

    /// <summary>Unregisters every hotkey (used before re-registering custom combinations).</summary>
    public void UnregisterAll()
    {
        foreach (int id in _registered)
        {
            NativeMethods.UnregisterHotKey(_msg.Handle, id);
        }
        _registered.Clear();
    }

    private void OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            HotkeyPressed?.Invoke(wParam.ToInt32());
        }
    }

    public void Dispose()
    {
        foreach (int id in _registered)
        {
            NativeMethods.UnregisterHotKey(_msg.Handle, id);
        }
        _registered.Clear();
        _msg.MessageReceived -= OnMessage;
    }
}
