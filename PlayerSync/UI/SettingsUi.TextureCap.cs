using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MareSynchronos.Services;
using MareSynchronos.Services.TextureCap;
using System.Numerics;

namespace MareSynchronos.UI;

// Fork (texture size cap): settings tab under Performance.
public partial class SettingsUi
{
    private string _textureCapExemptInput = string.Empty;

    private void DrawPerformanceTextureCap()
    {
        var cfg = VierAIConfigStore.Current;
        _uiShared.BigText("Texture Size Cap");
        ImGuiHelpers.ScaledDummy(2);
        UiSharedService.TextWrapped("Shrinks other players' textures that are only used on physically small models (rings, earrings, nails...). "
            + "The size comes from each model's bounding box; a texture shared by several models uses the largest one. "
            + "Textures that can't be tied to a modded model, index/ID maps, VFX and UI are never changed.");
        UiSharedService.ColorTextWrapped("Smaller copies are made in the background by dropping mip levels (no recompression) and are used from the next time that player is applied. Original files are never changed.", ImGuiColors.DalamudYellow);
        ImGui.Dummy(new Vector2(6));

        var enabled = cfg.TextureCapEnabled;
        if (ImGui.Checkbox("Enable texture size cap", ref enabled))
        {
            cfg.TextureCapEnabled = enabled;
            VierAIConfigStore.MarkDirty();
        }

        using (ImRaii.Disabled(!cfg.TextureCapEnabled))
        using (ImRaii.PushIndent())
        {
            DrawCapRow("Tiny", () => cfg.TextureCapTinyMaxCm, v => cfg.TextureCapTinyMaxCm = v, () => cfg.TextureCapTinyPx, v => cfg.TextureCapTinyPx = v);
            DrawCapRow("Small", () => cfg.TextureCapSmallMaxCm, v => cfg.TextureCapSmallMaxCm = v, () => cfg.TextureCapSmallPx, v => cfg.TextureCapSmallPx = v);
            DrawCapRow("Medium", () => cfg.TextureCapMediumMaxCm, v => cfg.TextureCapMediumMaxCm = v, () => cfg.TextureCapMediumPx, v => cfg.TextureCapMediumPx = v);
            UiSharedService.TextWrapped("Anything larger than the Medium size is not capped.");

            var eye = Array.IndexOf(CapSizes, cfg.TextureCapEyeFloorPx);
            if (eye < 0) eye = 3;
            ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo("Minimum size for eye textures", ref eye, CapLabels, CapLabels.Length))
            {
                cfg.TextureCapEyeFloorPx = CapSizes[eye];
                VierAIConfigStore.MarkDirty();
            }

            ImGui.Dummy(new Vector2(4));
            ImGui.TextUnformatted("Exempt UIDs/aliases (players in the performance override list are also exempt):");
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            ImGui.InputText("##texcapexempt", ref _textureCapExemptInput, 64);
            ImGui.SameLine();
            if (ImGui.Button("Add##texcapexempt") && !string.IsNullOrWhiteSpace(_textureCapExemptInput)
                && !cfg.TextureCapExemptUIDs.Contains(_textureCapExemptInput.Trim(), StringComparer.Ordinal))
            {
                cfg.TextureCapExemptUIDs.Add(_textureCapExemptInput.Trim());
                _textureCapExemptInput = string.Empty;
                VierAIConfigStore.MarkDirty();
            }
            foreach (var uid in cfg.TextureCapExemptUIDs.ToList())
            {
                ImGui.BulletText(uid);
                ImGui.SameLine();
                if (ImGui.SmallButton($"Remove##texcap{uid}"))
                {
                    cfg.TextureCapExemptUIDs.Remove(uid);
                    VierAIConfigStore.MarkDirty();
                }
            }
        }

        ImGui.Separator();
        var service = TextureCapService.Instance;
        if (service == null)
        {
            ImGui.TextUnformatted("Texture cap service is not running.");
            return;
        }

        var (files, bytes) = service.Cache.DiskUsage();
        ImGui.TextUnformatted($"Estimated VRAM saved (players currently applied): {UiSharedService.ByteToString(service.TotalSavedBytes)}");
        ImGui.TextUnformatted($"Smaller copies on disk: {files} ({UiSharedService.ByteToString(bytes)}), waiting jobs: {service.QueueLength}, skipped textures: {service.Cache.FailedCount}");
        if (ImGui.Button("Delete smaller copies"))
            service.ClearAll();
        UiSharedService.AttachToolTip("Deletes the files in the cache's downscaled folder. Players switch back to full-size textures on their next apply.");

        var rows = service.Stats.Where(s => s.Value.SavedBytes > 0 || s.Value.Capped > 0).OrderByDescending(s => s.Value.SavedBytes).ToList();
        if (rows.Count == 0) return;
        using var table = ImRaii.Table("##texcapstats", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
        if (!table) return;
        ImGui.TableSetupColumn("Player");
        ImGui.TableSetupColumn("Capped textures");
        ImGui.TableSetupColumn("In use");
        ImGui.TableSetupColumn("VRAM saved");
        ImGui.TableHeadersRow();
        foreach (var (_, s) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(s.Name);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(s.Capped.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(s.Swapped.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(UiSharedService.ByteToString(s.SavedBytes));
        }
    }

    private static readonly int[] CapSizes = [256, 512, 1024, 2048];
    private static readonly string[] CapLabels = ["256", "512", "1024", "2048"];

    private static void DrawCapRow(string label, Func<float> getCm, Action<float> setCm, Func<int> getPx, Action<int> setPx)
    {
        var cm = getCm();
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        if (ImGui.DragFloat($"cm  ##cm{label}", ref cm, 0.1f, 0.1f, 500f, "%.1f"))
        {
            setCm(Math.Clamp(cm, 0.1f, 500f));
            VierAIConfigStore.MarkDirty();
        }
        ImGui.SameLine();
        var index = Array.IndexOf(CapSizes, getPx());
        if (index < 0) index = 2;
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo($"px max ({label})##px{label}", ref index, CapLabels, CapLabels.Length))
        {
            setPx(CapSizes[index]);
            VierAIConfigStore.MarkDirty();
        }
    }
}
