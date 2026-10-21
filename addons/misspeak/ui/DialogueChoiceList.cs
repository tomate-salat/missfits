using System;
using Godot;

namespace Misspeak;

/// <summary>
/// One button per choice, in a column. The first one takes the focus, so the keyboard alone gets
/// through a dialogue.
/// </summary>
[GlobalClass]
public partial class DialogueChoiceList : VBoxContainer, IDialogueChoiceView {
    /// <summary>
    /// What a choice looks like: a scene whose root is a <see cref="BaseButton"/>. Its text is set
    /// when it is a <see cref="Button"/>. Left empty, a plain left-aligned Button.
    /// </summary>
    [Export]
    public PackedScene ButtonScene { get; set; }

    public event Action<int> Chosen;

    public void ShowChoices(string[] choices) {
        foreach (var child in GetChildren()) {
            RemoveChild(child);
            child.QueueFree();
        }

        Visible = choices.Length > 0;
        for (var i = 0; i < choices.Length; i++) {
            var button = ButtonScene?.Instantiate<BaseButton>() ?? new Button { Alignment = HorizontalAlignment.Left };
            if (button is Button labelled) labelled.Text = choices[i];
            var index = i;
            button.Pressed += () => Chosen?.Invoke(index);
            AddChild(button);
        }

        if (GetChildCount() > 0 && IsVisibleInTree()) (GetChild(0) as Control)?.GrabFocus();
    }
}
