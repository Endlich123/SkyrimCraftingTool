using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyrimCraftingTool.Services
{
    // One guided tour per tab: a list of steps, each pointing at a control by its Tutorial.Id
    // (View/Tutorial.cs) and saying what it is for. The texts live in Tutorials/<id>.json, embedded
    // in the assembly - a tour that ships separately is a tour that can go missing from the ZIP.
    public sealed class TutorialTour
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public List<TutorialStep> Steps { get; set; } = new();
    }

    public sealed class TutorialStep
    {
        // Tutorial.Id of the control to highlight. Empty: the callout stands in the middle, nothing
        // is cut out - for an introduction or a closing word.
        public string? Target { get; set; }

        public string Title { get; set; } = "";
        public string Text { get; set; } = "";

        // The deeper explanation behind "More about this". The step itself stays short; whoever wants
        // the rules and the tricks opens this.
        public string? More { get; set; }

        // Asked of the tab's view model (ITutorialHost) before the step is shown, e.g. "select an
        // example item". If it answers false, the tour stops with PrepareFailedText.
        public string? Prepare { get; set; }
        public string? PrepareFailedText { get; set; }

        // Tutorial.Ids of Expanders to open first. The detail sections start collapsed, and a control
        // inside a shut Expander has no place on screen to point at.
        public List<string>? Expand { get; set; }

        // Leave the step out when its target is not on screen - the weapon fields on an armor, say.
        // Otherwise the callout is shown in the middle with no cut-out.
        public bool SkipIfMissing { get; set; }
    }

    public static class TutorialService
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // Null when there is no tour for this id, or when it cannot be read - the button then says
        // so, and a broken tour never takes the app down.
        public static TutorialTour? Load(string id)
        {
            try
            {
                var asm = typeof(TutorialService).Assembly;
                var name = $"SkyrimCraftingTool.Tutorials.{id}.json";
                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null) return null;

                using var reader = new StreamReader(stream);
                return Parse(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Tutorial '{id}' could not be loaded", ex);
                return null;
            }
        }

        internal static TutorialTour? Parse(string json)
            => JsonSerializer.Deserialize<TutorialTour>(json, Options);
    }
}
