using Mutagen.Bethesda.Skyrim;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.ViewModel;
using System.Globalization;

public static class ConditionMapper
{
    public static BaseConditionViewModel ToViewModel(
        COBJConditionRecord rec,
        IEnumerable<FormIDRecord>? allAvailablePerks = null,
        IEnumerable<FormIDRecord>? allAvailableQuests = null)
    {
        if (rec == null) throw new ArgumentNullException(nameof(rec));

        BaseConditionViewModel vm = rec.ConditionType switch
        {
            "HasPerk" => new PerkConditionViewModel(),
            "GetIsSex" => new SexConditionViewModel(),
            "GetActorValue" => new ActorValueConditionViewModel(),
            "GetLevel" => new LevelConditionViewModel(),
            "GetStageDone" => new QuestStageConditionViewModel(),
            _ => throw new InvalidOperationException("Unknown condition type: " + rec.ConditionType)
        };

        // Set Type correctly
        vm.Type = rec.ConditionType switch
        {
            "HasPerk" => CustomConditionType.HasPerk,
            "GetIsSex" => CustomConditionType.GetIsSex,
            "GetActorValue" => CustomConditionType.GetActorValue,
            "GetLevel" => CustomConditionType.GetLevel,
            "GetStageDone" => CustomConditionType.GetStageDone,
            _ => throw new InvalidOperationException("Unknown condition type: " + rec.ConditionType)
        };

        vm.RunOnPlayer = rec.RunOn == "Subject";

        // Fill VM-specific fields
        switch (vm)
        {
            case PerkConditionViewModel perk:
                var perkFormKey = KeyFactory.ParseFormKey(rec.Target);
                perk.PerkFormKey = perkFormKey;
                perk.SelectedPerk = allAvailablePerks?.FirstOrDefault(p =>
                    KeyFactory.ParseFormKey(p.Key) == perkFormKey);
                perk.MustHavePerk = rec.Value == "1";
                break;

            case SexConditionViewModel sex:
                sex.TargetGender = rec.Target == "Male" ? Gender.Male : Gender.Female;
                break;

            case ActorValueConditionViewModel av:
                av.SelectedSkill = Enum.Parse<ActorValue>(rec.Target);
                av.RequiredValue = ParseFloat(rec.Value);
                break;

            case LevelConditionViewModel level:
                level.RequiredLevel = ParseFloat(rec.Value);
                break;

            case QuestStageConditionViewModel stage:
                var questFormKey = KeyFactory.ParseFormKey(rec.Target);
                stage.QuestFormKey = questFormKey;
                stage.SelectedQuest = allAvailableQuests?.FirstOrDefault(q =>
                    KeyFactory.ParseFormKey(q.Key) == questFormKey);
                stage.Stage = ParseInt(rec.Value);
                break;
        }

        return vm;
    }

    public static COBJConditionRecord ToRecord(BaseConditionViewModel vm, string cobjKey)
    {
        if (vm == null) throw new ArgumentNullException(nameof(vm));

        var runOn = vm.RunOnPlayer ? "Subject" : "Target";

        string conditionType = vm.Type switch
        {
            CustomConditionType.HasPerk => "HasPerk",
            CustomConditionType.GetIsSex => "GetIsSex",
            CustomConditionType.GetActorValue => "GetActorValue",
            CustomConditionType.GetLevel => "GetLevel",
            CustomConditionType.GetStageDone => "GetStageDone",
            _ => throw new InvalidOperationException("Unknown condition type: " + vm.Type)
        };

        var rec = new COBJConditionRecord
        {
            COBJKey = cobjKey,
            ConditionType = conditionType,
            RunOn = runOn
        };

        switch (vm)
        {
            case PerkConditionViewModel perk:
                rec.Target = KeyFactory.BuildMasterKey(perk.PerkFormKey);
                rec.Value = perk.MustHavePerk ? "1" : "0";
                rec.Extra = "";
                break;

            case SexConditionViewModel sex:
                rec.Target = sex.TargetGender.ToString();
                rec.Value = "1";
                rec.Extra = "";
                break;

            case ActorValueConditionViewModel av:
                rec.Target = av.SelectedSkill.ToString();
                rec.Value = av.RequiredValue.ToString(CultureInfo.InvariantCulture);
                rec.Extra = "";
                break;

            case LevelConditionViewModel level:
                rec.Target = "";
                rec.Value = level.RequiredLevel.ToString(CultureInfo.InvariantCulture);
                rec.Extra = "";
                break;

            case QuestStageConditionViewModel stage:
                rec.Target = KeyFactory.BuildMasterKey(stage.QuestFormKey);
                rec.Value = stage.Stage.ToString(CultureInfo.InvariantCulture);
                rec.Extra = "";
                break;
        }

        return rec;
    }

    // A condition the user started but never pointed at anything - HasPerk with no perk picked,
    // GetStageDone with no quest picked. Skipped when persisting a recipe (mirrors the empty-
    // ingredient filter) so a half-built condition doesn't come back as a broken one on reload.
    // GetLevel / GetIsSex / GetActorValue have no free-form target (or always have a value), so
    // they're always persistable.
    public static bool HasUsableTarget(BaseConditionViewModel vm) => vm switch
    {
        PerkConditionViewModel p => !p.PerkFormKey.IsNull,
        QuestStageConditionViewModel q => !q.QuestFormKey.IsNull,
        _ => true,
    };

    private static float ParseFloat(string value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0f;

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
}
