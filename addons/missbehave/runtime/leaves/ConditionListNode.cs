using System;
using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// Several conditions checked top to bottom inside one box — as a sequence (all must hold) or a
/// selector (one is enough).
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/condition_list.svg")]
public partial class ConditionListNode : AListNode {
    public override Type EntryType => typeof(ConditionNode);
}
