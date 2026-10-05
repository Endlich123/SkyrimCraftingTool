namespace SkyrimCraftingTool.Model;

// Which mod manager the user's setup is built around. Asked once in the folder dialog, because the
// answer changes ONE thing that cannot be read off the paths: which copy of a plugin file wins when
// the same name exists more than once (see Services/PluginPathPicker).
//
//   MO2   - keeps every copy on disk and lets its VFS decide at runtime. The tool has to REPLICATE
//           that decision, which is what modlist.txt is read for.
//   Other - Vortex, Wrye Bash, manual installs. All of them deploy: the conflict is already
//           resolved on disk and the copy in the game's Data folder IS the winner. Nothing to
//           replicate, so they need no further input.
//
// Deliberately only two real answers. Vortex had its own value for a while and it earned nothing:
// it took the identical code path to Other everywhere, so it was a third button that could only be
// answered wrongly.
//
// Unknown is what an older settings.json deserializes to - the field simply is not in it. It must
// therefore stay harmless: it selects the same rule as Other.
public enum ModManagerKind
{
    Unknown = 0,

    ModOrganizer2,

    Other,
}
