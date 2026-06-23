#nullable enable

namespace @TestProject;

internal static class GodotRegistryExtensions
{
    extension (global::Godot.Bridge.GodotRegistry)
    {
        internal static void InitializeUserTypes(global::Godot.Bridge.InitializationLevel level)
        {
            if (level != global::Godot.Bridge.InitializationLevel.Scene)
            {
                return;
            }
            global::Godot.Bridge.GodotRegistry.RegisterRuntimeClass<global::NS.MyNode>(global::NS.MyNode.BindMembers);
            global::Godot.Bridge.GodotRegistry.RegisterClass<global::NS.MyToolNode>(global::NS.MyToolNode.BindMembers);
            global::Godot.Bridge.GodotRegistry.RegisterAbstractClass<global::NS.MyAbstractNode>(global::NS.MyAbstractNode.BindMembers);
        }
        internal static void DeinitializeUserTypes(global::Godot.Bridge.InitializationLevel level)
        {
            if (level != global::Godot.Bridge.InitializationLevel.Scene)
            {
                return;
            }
        }
    }
}
