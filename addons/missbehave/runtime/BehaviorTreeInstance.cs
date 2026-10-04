using System;
using System.Collections.Generic;
using Misscore;

namespace Missbehave;

/// <summary>
/// One runner's private copy of a <see cref="BehaviorTree"/>. Holds the cloned node graph and the
/// per-frame status buffer the debugger streams.
/// </summary>
public sealed class BehaviorTreeInstance : INodeObserver {
    public BehaviorTree Definition { get; }

    /// <summary>Root of the cloned tree. Ticking this is safe; ticking the definition is not.</summary>
    public MissNode Root { get; }

    /// <summary>Clones in pre-order, indexed by <see cref="MissNode.RuntimeIndex"/>.</summary>
    public MissNode[] Flat { get; }

    /// <summary>Runtime index to definition id. Sent once on register so the editor can map back.</summary>
    public string[] IdTable { get; }

    /// <summary>Runtime index to parent index, -1 for the root. Sent once on register.</summary>
    public int[] ParentTable { get; }

    public string[] ClassNames { get; }

    internal byte[] Frame { get; }

    BehaviorTreeInstance(BehaviorTree definition, MissNode root, MissNode[] flat) {
        Definition = definition;
        Root = root;
        Flat = flat;
        Frame = new byte[flat.Length];

        IdTable = new string[flat.Length];
        ParentTable = new int[flat.Length];
        ClassNames = new string[flat.Length];
        Array.Fill(ParentTable, -1);

        for (var i = 0; i < flat.Length; i++) {
            IdTable[i] = flat[i].DefinitionId;
            ClassNames[i] = flat[i].GetType().Name;
            foreach (var child in flat[i].Children) {
                if (child != null) ParentTable[child.RuntimeIndex] = i;
            }
        }

        BeginFrame();
    }

    public static BehaviorTreeInstance Create(BehaviorTree definition) {
        if (definition?.Root == null) return null;

        var root = definition.Root.CloneRuntime();
        var flat = new List<MissNode>();
        BehaviorTree.Flatten(root, flat);
        return new BehaviorTreeInstance(definition, root, [.. flat]);
    }

    internal void BeginFrame() => Array.Fill(Frame, MissStatusExtensions.NotTicked);

    public void Report(int index, MissStatus status) {
        if (index >= 0 && index < Frame.Length) Frame[index] = (byte) status;
    }
}
