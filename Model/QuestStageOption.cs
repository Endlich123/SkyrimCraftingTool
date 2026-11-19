namespace SkyrimCraftingTool.Model
{
    // One row of the stage picker in the GetStageDone / GetStage conditions: the index, and the
    // journal text the stage writes (the QUST log entry, CNAM). A stage has no description of its
    // own - the log entry is the only text attached to it, and only stages that write to the
    // journal have one. Measured 2026-11-08 on the real modlist: 163 of 209 stages that a
    // GetStageDone recipe condition asks for have a text, 59 of 199 for GetStage.
    public sealed class QuestStageOption
    {
        private const int LabelLength = 70;

        public QuestStageOption(int index, string? text)
        {
            Index = index;
            Text = text?.Trim() ?? "";
        }

        public int Index { get; }

        // All log entries of the stage, blank line between them - a stage can have several,
        // picked by their conditions.
        public string Text { get; }

        public bool HasText => Text.Length > 0;

        // The first line, shortened - grey after the index in the dropdown.
        public string Label
        {
            get
            {
                if (!HasText) return "";
                var line = Text.Split('\n')[0].Trim();
                return line.Length > LabelLength ? line[..LabelLength].TrimEnd() + "…" : line;
            }
        }

        public string ToolTipText => HasText ? $"Stage {Index}\n\n{Text}" : $"Stage {Index} - no journal entry";

        // The editable ComboBox writes TextSearch.TextPath (Index) into the box; this is the fallback.
        public override string ToString() => Index.ToString();

        public static IReadOnlyList<QuestStageOption> For(FormIDRecord? quest)
        {
            if (quest?.Stages is not { Count: > 0 } stages) return Array.Empty<QuestStageOption>();
            return stages
                .Select(i => new QuestStageOption(i, quest.StageTexts != null && quest.StageTexts.TryGetValue(i, out var t) ? t : null))
                .ToList();
        }

        // Tooltip of the stage box: the text of the stage the value points at.
        public static string ToolTipFor(FormIDRecord? quest, float value)
        {
            if (quest?.Stages is not { Count: > 0 }) return "Stage of the quest - or type one.";

            int index = (int)value;
            if (index == value && quest.Stages.Contains(index))
            {
                var text = quest.StageTexts != null && quest.StageTexts.TryGetValue(index, out var t) ? t : null;
                return new QuestStageOption(index, text).ToolTipText;
            }
            return "Stages defined by the selected quest - or type one.";
        }
    }
}
