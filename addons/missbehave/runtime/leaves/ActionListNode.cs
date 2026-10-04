using System;
using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// Several actions run top to bottom inside one box — as a sequence (until one fails) or a selector
/// (until one succeeds). An action that is still running is resumed on the next tick.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/action_list.svg")]
public partial class ActionListNode : AListNode {
    public override Type EntryType => typeof(ActionNode);

    /// <summary>Filed with what it holds: an action list is used like an action.</summary>
    public override string PickerGroup => NodeGroup.Action;
}
