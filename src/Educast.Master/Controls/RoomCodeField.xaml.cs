using System.Diagnostics;
using Educast.Master.Core.Setup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Educast.Master.Controls;

/// <summary>
/// The room code field: six digits, masked, with a reveal eye — a Windows password field that
/// refuses an input before taking it.
/// </summary>
/// <remarks>
/// <b>Why not the stock PasswordBox.</b> It has no caret or selection API, it cuts a paste to
/// <c>MaxLength</c> before any handler sees it, and setting <c>Password</c> from code moves the
/// caret to the start and hides its reveal button. So a refused keystroke could not be made to
/// change nothing, and a normalised paste could not be made to land without losing the eye.
///
/// <b>How this one works.</b> A <see cref="TextBox"/> shows one mask dot per digit (the digits
/// themselves while the eye is held). Every edit the control proposes is intercepted in
/// <c>BeforeTextChanging</c> before it happens, decided by <see cref="RoomCodeEntry.Edit"/>
/// from the code, the caret and the selection, and always cancelled; an accepted edit is then
/// applied by this class — the new face and the caret <see cref="RoomCodeEntry"/> says. A paste
/// is taken over in the <c>Paste</c> event the same way, through <see cref="RoomCodeEntry.Paste"/>.
/// A refused edit therefore never reaches the control: content, caret, selection and eye all
/// stay as they were. The code is held in <see cref="Code"/> and nowhere else; while masked,
/// UI Automation sees the dots.
///
/// <b>Where it differs from the stock control</b>: the eye shows whenever the field has content,
/// not only while the field has focus; the eye has an automation name of its own; the field's
/// text is applied one dispatcher step after the keystroke that caused it (never visible, but
/// it is how the control is kept out of its own change handler); and the last-resort check in
/// <c>TextChanged</c>, for text that arrives by a path <c>BeforeTextChanging</c> does not see
/// (an input method, a dead key), restores the face and may move the caret to the end.
/// </remarks>
public sealed partial class RoomCodeField : UserControl
{
    private string _code = string.Empty;
    private bool _revealed;
    private bool _applying;
    private int? _pendingCaret;
    private VirtualKey _lastKey;

    public RoomCodeField()
    {
        InitializeComponent();

        // The stock eye reveals while pressed (PasswordRevealMode.Peek). A Button swallows the
        // pointer events itself, so they are listened for handled-events-too.
        RevealButton.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => SetRevealed(true)), true);
        RevealButton.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => SetRevealed(false)), true);
        RevealButton.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => SetRevealed(false)), true);
        RevealButton.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => SetRevealed(false)), true);
        RevealButton.AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => SetRevealed(false)), true);
    }

    /// <summary>Raised after <see cref="Code"/> changed through the field.</summary>
    public event EventHandler? CodeChanged;

    /// <summary>The digits the field holds. Setting it replaces the content; the caret goes to the end.</summary>
    public string Code
    {
        get => _code;
        set
        {
            var code = value ?? string.Empty;
            if (code == _code)
            {
                return;
            }

            _code = code;
            Apply(_code.Length, raise: false);
        }
    }

    /// <summary>The name a screen reader gives the field.</summary>
    public string AccessibleName
    {
        get => AutomationProperties.GetName(Box);
        set => AutomationProperties.SetName(Box, value);
    }

    private string Face => _revealed ? _code : RoomCodeEntry.Mask(_code);

    private void Box_PreviewKeyDown(object sender, KeyRoutedEventArgs e) => _lastKey = e.Key;

    /// <summary>
    /// Every edit the control is about to make: decided here, from the code and the selection,
    /// and always cancelled; what is accepted is applied by <see cref="Apply"/>.
    /// </summary>
    private void Box_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        if (_applying)
        {
            return;
        }

        args.Cancel = true;

        // The selection as it stands before the edit; or, when an accepted edit has not been
        // applied yet (two keystrokes in one dispatcher step), where its caret will be.
        var start = _pendingCaret ?? Box.SelectionStart;
        var length = _pendingCaret is null ? Box.SelectionLength : 0;
        var proposed = args.NewText ?? string.Empty;
        var removedByReplace = length;
        var insertedLength = proposed.Length - (_code.Length - removedByReplace);

        if (insertedLength < 0)
        {
            // A deletion with no selection: Backspace takes the characters before the caret,
            // Delete the ones after. The control's text alone cannot say which, so the last key
            // does. Ctrl+Backspace / Ctrl+Delete remove more than one.
            var removed = -insertedLength;
            start = _lastKey == VirtualKey.Delete ? start : Math.Max(0, start - removed);
            length = removed;
            insertedLength = 0;
        }

        if (insertedLength == 0 && length == 0)
        {
            return;
        }

        var inserted = start <= proposed.Length && start + insertedLength <= proposed.Length
            ? proposed.Substring(start, insertedLength)
            : proposed;

        if (RoomCodeEntry.Edit(_code, start, length, inserted) is { } edit)
        {
            _code = edit.Text;
            Apply(edit.Caret, raise: true);
        }
    }

    /// <summary>A paste, taken over before the control does its own: decided by <see cref="RoomCodeEntry.Paste"/>.</summary>
    private async void Box_Paste(object sender, TextControlPasteEventArgs e)
    {
        e.Handled = true;
        var start = _pendingCaret ?? Box.SelectionStart;
        var length = _pendingCaret is null ? Box.SelectionLength : 0;

        string? text = null;
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                text = await content.GetTextAsync();
            }
        }
        catch (Exception ex)
        {
            // The clipboard belongs to whoever holds it; a refused read is a refused paste.
            Trace.TraceError("Room code field: the clipboard could not be read: {0}", ex);
        }

        if (text is not null && RoomCodeEntry.Paste(_code, start, length, text) is { } edit)
        {
            _code = edit.Text;
            Apply(edit.Caret, raise: true);
        }
    }

    /// <summary>
    /// The last resort: text that arrived by a path BeforeTextChanging did not see. The face is
    /// put back; this is the one path that may move the caret.
    /// </summary>
    private void Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_applying && _pendingCaret is null && Box.Text != Face)
        {
            Apply(_code.Length, raise: false);
        }
    }

    /// <summary>
    /// Puts the face and the caret on the control. Deferred one dispatcher step, so the control
    /// is never written to from inside its own change handler.
    /// </summary>
    private void Apply(int caret, bool raise)
    {
        _pendingCaret = caret;
        DispatcherQueue.TryEnqueue(() =>
        {
            _applying = true;
            try
            {
                Box.Text = Face;
                Box.SelectionStart = Math.Clamp(caret, 0, Box.Text.Length);
                Box.SelectionLength = 0;
            }
            finally
            {
                _applying = false;
                _pendingCaret = null;
            }

            RevealButton.Visibility = _code.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        });

        if (raise)
        {
            CodeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetRevealed(bool revealed)
    {
        if (_revealed == revealed)
        {
            return;
        }

        _revealed = revealed;
        Apply(_pendingCaret ?? Box.SelectionStart, raise: false);
    }
}
