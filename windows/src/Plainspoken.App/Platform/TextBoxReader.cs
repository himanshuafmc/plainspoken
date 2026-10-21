using System.Runtime.InteropServices;

namespace Plainspoken.App.Platform;

/// <summary>
/// Reads the text of the text box that has keyboard focus, through Windows UI Automation (the accessibility interface
/// screen readers use). Used only to spot words the user corrected after a dictation; the text is compared in memory
/// and never stored, logged or sent anywhere. Password boxes are never read.
/// Talks to UIAutomationCore through a few COM declarations (see <see cref="Uia"/>), so the app needs no WPF.
/// </summary>
internal static class TextBoxReader
{
    private const int MaxChars = 200_000;

    /// <summary>The focused text box's text, or null if there is none, it can't be read, or it takes too long.</summary>
    public static string? ReadFocused(TimeSpan timeout)
    {
        var task = Task.Run(Read);
        try
        {
            return task.Wait(timeout) ? task.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    private static string? Read()
    {
        try
        {
            var automation = (Uia.IUIAutomation)new Uia.CUIAutomation();
            var element = automation.GetFocusedElement();
            if (element is null ||
                element.GetCurrentPropertyValue(Uia.IsPasswordPropertyId) is true ||
                element.GetCurrentPropertyValue(Uia.ProcessIdPropertyId) is int pid && pid == Environment.ProcessId)
            {
                return null;
            }

            if (element.GetCurrentPattern(Uia.TextPatternId) is Uia.IUIAutomationTextPattern text)
            {
                return text.DocumentRange().GetText(MaxChars);
            }

            if (element.GetCurrentPattern(Uia.ValuePatternId) is Uia.IUIAutomationValuePattern value)
            {
                return value.CurrentValue();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            // The app doesn't expose its text, or the box went away: nothing to learn this time.
        }

        return null;
    }
}

/// <summary>
/// The parts of UIAutomationClient.h that <see cref="TextBoxReader"/> uses. COM finds methods by their position, so every
/// interface lists its methods in the header's order; the ones Plainspoken never calls are placeholders that only keep
/// the positions right and must never be called.
/// </summary>
internal static class Uia
{
    public const int ValuePatternId = 10002;
    public const int TextPatternId = 10014;
    public const int ProcessIdPropertyId = 30002;
    public const int IsPasswordPropertyId = 30019;

    [ComImport]
    [Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    public class CUIAutomation
    {
    }

    [ComImport]
    [Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomation
    {
        void CompareElements();

        void CompareRuntimeIds();

        void GetRootElement();

        void ElementFromHandle();

        void ElementFromPoint();

        IUIAutomationElement? GetFocusedElement();
    }

    [ComImport]
    [Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationElement
    {
        void SetFocus();

        void GetRuntimeId();

        void FindFirst();

        void FindAll();

        void FindFirstBuildCache();

        void FindAllBuildCache();

        void BuildUpdatedCache();

        [return: MarshalAs(UnmanagedType.Struct)]
        object? GetCurrentPropertyValue(int propertyId);

        void GetCurrentPropertyValueEx();

        void GetCachedPropertyValue();

        void GetCachedPropertyValueEx();

        void GetCurrentPatternAs();

        void GetCachedPatternAs();

        [return: MarshalAs(UnmanagedType.IUnknown)]
        object? GetCurrentPattern(int patternId);
    }

    [ComImport]
    [Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationValuePattern
    {
        void SetValue();

        [return: MarshalAs(UnmanagedType.BStr)]
        string CurrentValue();
    }

    [ComImport]
    [Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationTextPattern
    {
        void RangeFromPoint();

        void RangeFromChild();

        void GetSelection();

        void GetVisibleRanges();

        IUIAutomationTextRange DocumentRange();
    }

    [ComImport]
    [Guid("a543cc6a-f4ae-494b-8239-c814481187a8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationTextRange
    {
        void Clone();

        void Compare();

        void CompareEndpoints();

        void ExpandToEnclosingUnit();

        void FindAttribute();

        void FindText();

        void GetAttributeValue();

        void GetBoundingRectangles();

        void GetEnclosingElement();

        [return: MarshalAs(UnmanagedType.BStr)]
        string GetText(int maxLength);
    }
}
