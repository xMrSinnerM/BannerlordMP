using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BannerlordMP.Ui
{
    internal sealed class Choice<T>
    {
        public Choice(T value, string label, bool enabled = true, string hint = null)
        {
            Value = value;
            Label = label;
            Enabled = enabled;
            Hint = hint;
        }

        public T Value { get; }
        public string Label { get; }
        public bool Enabled { get; }
        public string Hint { get; }
    }

    /// <summary>Thin wrappers over the game's own inquiry popups, which work both in the main menu and on the map.</summary>
    internal static class Dialogs
    {
        public static void Message(string title, string text, Action ok = null)
        {
            InformationManager.ShowInquiry(new InquiryData(title, text, true, false, "OK", null, ok, null, "", 0f, null, null, null), true, false);
        }

        public static void Confirm(string title, string text, string yes, string no, Action onYes, Action onNo = null)
        {
            InformationManager.ShowInquiry(new InquiryData(title, text, true, true, yes, no, onYes, onNo, "", 0f, null, null, null), true, false);
        }

        public static void Text(string title, string text, Action<string> ok, Action cancel = null, bool password = false,
            string defaultText = "", Func<string, string> validate = null)
        {
            Func<string, Tuple<bool, string>> condition = null;
            if (validate != null)
            {
                condition = input =>
                {
                    var error = validate(input);
                    return Tuple.Create(error == null, error ?? string.Empty);
                };
            }
            InformationManager.ShowTextInquiry(new TextInquiryData(title, text, true, true, "OK", "Back",
                input => ok(input ?? string.Empty), cancel, password, condition, "", defaultText ?? string.Empty), true, false);
        }

        public static void Choose<T>(string title, string text, List<Choice<T>> choices, Action<T> ok, Action cancel = null, string okText = "Select")
        {
            var elements = choices.Select(c => new InquiryElement(c.Value, c.Label, null, c.Enabled, c.Hint ?? string.Empty)).ToList();
            MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(title, text, elements, true, 1, 1, okText, "Back",
                selected =>
                {
                    if (selected != null && selected.Count > 0)
                        ok((T)selected[0].Identifier);
                },
                _ => cancel?.Invoke(), "", false), true, false);
        }
    }
}
