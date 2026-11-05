using System.Collections.Generic;

namespace SkyrimCraftingTool.ViewModel
{
    // OR groups in a condition list, the way Skyrim evaluates them: a row with the OR flag is OR'ed
    // with the NEXT row, runs of those form a group, and the groups are AND'ed together.
    //
    //     A [OR]          ┐
    //     B               ┘  (A or B)
    //     C                  and C
    //
    // On the real modlist (2026-10-24) there are 6,250 two-row groups, a handful up to 13 rows long,
    // and 391 recipes whose LAST row carries an OR with nothing after it.
    public static class ConditionGroups
    {
        // Sets GroupPosition / Connector / IsDanglingOr on every row. Presentation only.
        public static void Apply(IReadOnlyList<BaseConditionViewModel> rows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                bool linksBack = i > 0 && rows[i - 1].HasOrFlag;
                bool linksForward = rows[i].HasOrFlag && i < rows.Count - 1;

                rows[i].GroupPosition = (linksBack, linksForward) switch
                {
                    (false, false) => ConditionGroupPosition.Alone,
                    (false, true) => ConditionGroupPosition.First,
                    (true, true) => ConditionGroupPosition.Middle,
                    (true, false) => ConditionGroupPosition.Last,
                };
                rows[i].Connector = i == 0 ? "" : linksBack ? "OR" : "AND";
                rows[i].IsDanglingOr = rows[i].HasOrFlag && i == rows.Count - 1;
            }
        }

        // Removes a row without changing what the rows around it mean.
        //
        // Deleting the LAST member of a group (no OR itself, the row before has one) would leave
        // that row's OR pointing at whatever follows: "not enchanted OR Arcane Blacksmith" silently
        // becomes "not enchanted OR <next condition>". So that OR goes with it. Deleting a first or
        // middle member needs nothing - the chain closes over the gap.
        //
        // Returns the row whose OR was cleared, or null.
        public static BaseConditionViewModel? Remove(IList<BaseConditionViewModel> rows, BaseConditionViewModel row)
        {
            int i = rows.IndexOf(row);
            if (i < 0) return null;

            BaseConditionViewModel? cleared = null;
            if (i > 0 && !row.HasOrFlag && rows[i - 1].HasOrFlag)
            {
                cleared = rows[i - 1];
                cleared.ClearOrFlag();
            }

            rows.RemoveAt(i);
            return cleared;
        }
    }
}
