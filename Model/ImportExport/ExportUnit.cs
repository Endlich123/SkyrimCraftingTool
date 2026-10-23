using Microsoft.Data.Sqlite;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Model
{
    // What a unit finds locally for one imported DTO. The routing into Apply / SkipEqual /
    // SkipMissing / SkipInvalid / Conflict is done ONCE, in ItemDBHandler.PreviewImport, from this
    // answer - it used to be spelled out per table (once for the keyed tables, once more for the
    // worn-restriction lists), which is how two copies of the same rule drift apart.
    public enum ImportTargetState
    {
        // Nothing local to attach to - usually the owning plugin is not installed. Skipped.
        Missing,

        // Nothing local, but the DTO carries everything needed to create it (a user-created
        // record). Applied without a conflict check.
        Create,

        // Present locally and never edited here. Applied without a conflict check.
        Untouched,

        // Present locally and edited here at LocalLastChanged. Compared against the file.
        Edited,

        // For records with no timestamp to compare (NPC groups, TE-5): the unit compared the
        // CONTENT instead. Identical is skipped as equal; Differs is always a conflict - with no
        // time to say which side is newer, the user decides.
        Identical,
        Differs,
    }

    public readonly record struct ImportTarget(ImportTargetState State, string LocalLastChanged = "")
    {
        public static readonly ImportTarget Missing = new(ImportTargetState.Missing);
        public static readonly ImportTarget Create = new(ImportTargetState.Create);
        public static readonly ImportTarget Untouched = new(ImportTargetState.Untouched);
        public static ImportTarget Edited(string lastChanged) => new(ImportTargetState.Edited, lastChanged);
        public static readonly ImportTarget Identical = new(ImportTargetState.Identical);
        public static readonly ImportTarget Differs = new(ImportTargetState.Differs);

        // A row that exists, read as its LastChanged column: NULL/empty means never edited.
        public static ImportTarget FromLastChanged(string? lastChanged) =>
            string.IsNullOrEmpty(lastChanged) ? Untouched : Edited(lastChanged);
    }

    // One kind of exportable edit ("Armor", "COBJ", "Enchantments" ...). docs/TabExport-Plan.md §3.1.
    //
    // A unit owns all three halves of its edit: what counts as edited (Collect), what it finds
    // locally (Probe) and how it writes an imported DTO back (Apply). Adding a unit is adding a
    // class and listing it in a tab's module - not a fourth switch branch in three places, which is
    // the shape docs/ImportExport-Analyse.md traced four data-loss bugs back to.
    public abstract class ExportUnit
    {
        // Written into every DTO this unit exports and used to route an imported DTO back to it.
        // Must stay stable: it is part of the on-disk file format.
        public abstract string Table { get; }

        // Every edited record of this unit within the scope. Scope Item/Plugin filters by record
        // key; a unit whose keys don't fit that model returns nothing for those scopes.
        internal abstract List<EditedItemDto> Collect(SqliteConnection connection, ExportScope scope, string? scopeValue);

        internal abstract ImportTarget Probe(SqliteConnection connection, EditedItemDto item);

        // Returns true only if the DTO actually caused a write - drives ImportResult.Applied.
        // Runs inside the import's single transaction; a throw rolls back the whole import.
        internal abstract bool Apply(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item);

        // Import, step 1 of 2, before anything is probed: a unit whose records carry a locally
        // handed-out Key (the user's own recipes, later own leveled lists) settles that Key here -
        // rewriting item.Key - and records every change in the map.
        internal virtual void ResolveKeys(SqliteConnection connection, IReadOnlyList<EditedItemDto> items, ImportKeyMap map) { }

        // Import, step 2 of 2: a unit that POINTS at such a record rewrites its references through
        // the map. Runs after every unit's ResolveKeys, so the order of units does not matter.
        //
        // `notes` collects what the summary should tell the user - a reference that could not be
        // resolved, say. They are shown, never fatal.
        internal virtual void FollowKeys(SqliteConnection connection, IReadOnlyList<EditedItemDto> items, ImportKeyMap map, List<string> notes) { }

        public override string ToString() => Table;
    }

    // Keys an import changed on the way in, per table: "the file's SkyrimCraftingTool.esp|0DA217
    // is this machine's SkyrimCraftingTool.esp|0DA21A". One map per import run.
    public sealed class ImportKeyMap
    {
        private readonly Dictionary<(string Table, string Key), string> _map = new();

        public void Add(string table, string fromKey, string toKey) =>
            _map[(table, fromKey.ToUpperInvariant())] = toKey;

        // The key to use here: the remapped one, or the one given.
        public string Resolve(string table, string key) =>
            !string.IsNullOrEmpty(key) && _map.TryGetValue((table, key.ToUpperInvariant()), out var to) ? to : key;

        public int Count => _map.Count;

        // Every user-created record in THIS import, by Uid -> the Key it will have here, whether that
        // Key changed or not. A reference to a record that the same import creates cannot be found in
        // the database yet (nothing is applied before the preview), so it is found here - an item
        // placed in an own list exported together with it, say (TE-6).
        private readonly Dictionary<(string Table, string Uid), string> _byUid = new();

        public void RememberUid(string table, string uid, string key)
        {
            if (!string.IsNullOrEmpty(uid)) _byUid[(table, uid)] = key;
        }

        public string? KeyForUid(string table, string uid) =>
            !string.IsNullOrEmpty(uid) && _byUid.TryGetValue((table, uid), out var key) ? key : null;
    }
}
