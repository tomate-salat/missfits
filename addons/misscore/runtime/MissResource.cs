using Godot;

namespace Misscore;

/// <summary>
/// Base of every resource the Missfits addons define. It adds nothing: it is there so that Godot's
/// class lists — the "New Resource" dialog, for one — show them together under one entry instead of
/// scattered among everything else that is a Resource.
/// </summary>
[GlobalClass, Tool]
public abstract partial class MissResource : Resource;