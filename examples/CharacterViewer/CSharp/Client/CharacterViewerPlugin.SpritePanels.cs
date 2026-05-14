using Barotrauma;
using Barotrauma.Extensions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CharacterViewer;

public sealed partial class CharacterViewerPlugin
{
    private readonly HashSet<GUIListBox> spriteListsPendingScrollReset = new HashSet<GUIListBox>();
    private readonly Dictionary<GUIListBox, GUIScrollBar> spriteHorizontalScrollBars = new Dictionary<GUIListBox, GUIScrollBar>();
    private readonly Dictionary<GUIListBox, float> spriteHorizontalScrollOffsets = new Dictionary<GUIListBox, float>();
    private readonly Dictionary<GUIListBox, int> spriteCanvasWidths = new Dictionary<GUIListBox, int>();
    private readonly HashSet<XElement> dirtySpriteElements = new HashSet<XElement>();
    private readonly HashSet<XElement> materializedSourceRectElements = new HashSet<XElement>();
    private readonly HashSet<XElement> materializedOriginElements = new HashSet<XElement>();
    private readonly Dictionary<XElement, Rectangle> editedSourceRects = new Dictionary<XElement, Rectangle>();
    private readonly Dictionary<XElement, Vector2> editedOrigins = new Dictionary<XElement, Vector2>();
    private readonly Dictionary<XElement, Sprite> liveSpritesByElement = new Dictionary<XElement, Sprite>();
    private readonly Dictionary<XElement, ContentPackage> sourcePackagesByElement = new Dictionary<XElement, ContentPackage>();

    private GUIListBox clothingSpriteSelectionList;
    private GUITextBlock selectedSpriteTitleText;
    private GUITextBlock selectedSpriteStatusText;
    private GUINumberInput sourceXInput;
    private GUINumberInput sourceYInput;
    private GUINumberInput sourceWidthInput;
    private GUINumberInput sourceHeightInput;
    private GUINumberInput originXInput;
    private GUINumberInput originYInput;
    private XElement selectedSpriteElement;
    private bool suppressSpriteEditorEvents;

    private sealed class ViewerSpriteEntry
    {
        public string Title;
        public string Subtitle;
        public Sprite Sprite;
        public Rectangle SourceRect;
        public Vector2 Origin;
        public float Scale = 1.0f;
        public bool InheritSourceRect;
        public bool InheritOrigin;
        public string FilePath;
        public ContentXElement SourceElement;
        public WearableSprite WearableSprite;
        public bool IsEditable;
        public bool IsDirty;

        public string Tooltip =>
            $"{Title}\nFile: {Path.GetFileName(FilePath)}\nRect: {SourceRect.X}, {SourceRect.Y}, {SourceRect.Width}, {SourceRect.Height}{(InheritSourceRect ? " (inherited)" : string.Empty)}\nOrigin: {Origin.X:0.##}, {Origin.Y:0.##}{(InheritOrigin ? " (inherited)" : string.Empty)}\nScale: {Scale:0.###}";
    }

    private void CreateBodySpriteWindow()
    {
        GUILayoutGroup content = CreateFloatingWindow("Body Sprite", new Point(420, 250), new Point(1200, 15), out bodySpriteWindow);
        bodySpriteInfoList = CreateSpritePreviewPanel(content, "Body Sprites", bodySpritePreviewZoom, value => bodySpritePreviewZoom = value);
    }

    private void CreateHeadSpriteWindow()
    {
        GUILayoutGroup content = CreateFloatingWindow("Head Sprite", new Point(420, 360), new Point(1200, 265), out headSpriteWindow);
        headSpriteInfoList = CreateSpritePreviewPanel(content, "Head Sprites", headSpritePreviewZoom, value => headSpritePreviewZoom = value);
    }

    private void CreateClothingSpriteWindow()
    {
        GUILayoutGroup content = CreateFloatingWindow("Clothing Sprite", new Point(420, 400), new Point(1200, 625), out clothingSpriteWindow);
        CreateWearableSpriteEditor(content);
        clothingSpriteInfoList = CreateSpritePreviewPanel(content, "Clothing Sprites", clothingSpritePreviewZoom, value => clothingSpritePreviewZoom = value);
    }

    private void CreateWearableSpriteEditor(GUILayoutGroup content)
    {
        selectedSpriteTitleText = new GUITextBlock(
            new RectTransform(new Vector2(1.0f, 0.0f), content.RectTransform),
            "No clothing sprite selected.",
            font: GUIStyle.SmallFont,
            textAlignment: Alignment.CenterLeft)
        {
            CanBeFocused = false
        };
        selectedSpriteTitleText.RectTransform.MinSize = new Point(0, GUI.IntScale(18));

        clothingSpriteSelectionList = new GUIListBox(new RectTransform(new Vector2(1.0f, 0.0f), content.RectTransform), style: null)
        {
            Padding = Vector4.Zero
        };
        clothingSpriteSelectionList.RectTransform.MinSize = new Point(0, GUI.IntScale(58));
        clothingSpriteSelectionList.OnSelected = (_, data) =>
        {
            if (data is ViewerSpriteEntry entry)
            {
                SelectSpriteEntry(entry);
            }
            return true;
        };

        GUILayoutGroup sourceRow = CreateNumberInputRow(content, "Rect", out sourceXInput, out sourceYInput, out sourceWidthInput, out sourceHeightInput, integerInputs: true);
        sourceWidthInput.MinValueInt = 1;
        sourceHeightInput.MinValueInt = 1;
        _ = sourceRow;

        CreateNumberInputRow(content, "Origin", out originXInput, out originYInput, out _, out _, integerInputs: false);
        originXInput.MinValueFloat = -10.0f;
        originXInput.MaxValueFloat = 10.0f;
        originYInput.MinValueFloat = -10.0f;
        originYInput.MaxValueFloat = 10.0f;

        foreach (GUINumberInput input in GetSpriteEditorInputs())
        {
            input.OnValueChanged += _ => ApplySpriteEditorInputs();
            input.OnValueEntered += _ => ApplySpriteEditorInputs();
        }

        GUILayoutGroup buttonRow = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.0f), content.RectTransform), isHorizontal: true)
        {
            Stretch = true,
            RelativeSpacing = 0.02f
        };
        buttonRow.RectTransform.MinSize = new Point(0, GUI.IntScale(24));
        CreateButton(buttonRow, "Save XML", SaveDirtySpriteXml);
        CreateButton(buttonRow, "Revert", RevertSelectedSpriteEdits);

        selectedSpriteStatusText = new GUITextBlock(
            new RectTransform(new Vector2(1.0f, 0.0f), content.RectTransform),
            "",
            wrap: true,
            font: GUIStyle.SmallFont)
        {
            TextColor = GUIStyle.TextColorDim,
            CanBeFocused = false
        };
        selectedSpriteStatusText.RectTransform.MinSize = new Point(0, GUI.IntScale(28));
    }

    private static GUILayoutGroup CreateNumberInputRow(GUILayoutGroup parent, string label, out GUINumberInput first, out GUINumberInput second, out GUINumberInput third, out GUINumberInput fourth, bool integerInputs)
    {
        GUILayoutGroup row = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.0f), parent.RectTransform), isHorizontal: true, childAnchor: Anchor.CenterLeft)
        {
            Stretch = true,
            RelativeSpacing = 0.015f
        };
        row.RectTransform.MinSize = new Point(0, GUI.IntScale(24));
        new GUITextBlock(new RectTransform(new Vector2(0.18f, 1.0f), row.RectTransform), label, font: GUIStyle.SmallFont, textAlignment: Alignment.CenterLeft)
        {
            CanBeFocused = false
        };

        NumberType numberType = integerInputs ? NumberType.Int : NumberType.Float;
        first = CreateNumberInput(row, numberType);
        second = CreateNumberInput(row, numberType);
        third = integerInputs ? CreateNumberInput(row, numberType) : null;
        fourth = integerInputs ? CreateNumberInput(row, numberType) : null;
        return row;
    }

    private static GUINumberInput CreateNumberInput(GUILayoutGroup parent, NumberType numberType)
    {
        var input = new GUINumberInput(new RectTransform(new Vector2(0.2f, 1.0f), parent.RectTransform), numberType, buttonVisibility: GUINumberInput.ButtonVisibility.ForceHidden)
        {
            ValueStep = numberType == NumberType.Int ? 1.0f : 0.01f,
            DecimalsToDisplay = numberType == NumberType.Int ? 0 : 3
        };
        return input;
    }

    private GUIListBox CreateSpritePreviewPanel(GUILayoutGroup content, string label, float zoom, Action<float> setZoom)
    {
        var panel = new GUIFrame(new RectTransform(Vector2.One, content.RectTransform), style: null);
        panel.RectTransform.MinSize = new Point(0, GUI.IntScale(334));

        var zoomRow = new GUIFrame(new RectTransform(new Vector2(1.0f, 0.0f), panel.RectTransform, Anchor.TopLeft, Pivot.TopLeft), style: null);
        zoomRow.RectTransform.MinSize = new Point(0, GUI.IntScale(28));
        var zoomLabel = new GUITextBlock(
            new RectTransform(new Vector2(0.22f, 1.0f), zoomRow.RectTransform, Anchor.CenterLeft),
            $"Zoom: {(int)(zoom * 100)}%",
            font: GUIStyle.SmallFont,
            textAlignment: Alignment.CenterLeft)
        {
            CanBeFocused = false
        };
        var slider = new GUIScrollBar(
            new RectTransform(new Vector2(0.36f, 0.55f), zoomRow.RectTransform, Anchor.CenterLeft)
            {
                RelativeOffset = new Vector2(0.23f, 0.0f)
            },
            barSize: 0.12f,
            isHorizontal: true)
        {
            Range = new Vector2(0.25f, 2.0f),
            StepValue = 0.05f,
            BarScrollValue = zoom
        };
        slider.OnMoved = (scrollBar, _) =>
        {
            float newZoom = scrollBar.BarScrollValue;
            setZoom(newZoom);
            zoomLabel.Text = $"Zoom: {(int)(newZoom * 100)}%";
            UpdateAllViewerSpriteInfo();
            return true;
        };
        new GUITextBlock(
            new RectTransform(new Vector2(0.38f, 1.0f), zoomRow.RectTransform, Anchor.CenterRight),
            label,
            font: GUIStyle.SmallFont,
            textAlignment: Alignment.CenterRight)
        {
            CanBeFocused = false
        };

        GUIListBox list = null;
        var horizontalScrollBar = new GUIScrollBar(
            new RectTransform(new Vector2(1.0f, 0.0f), panel.RectTransform, Anchor.TopLeft, Pivot.TopLeft)
            {
                AbsoluteOffset = new Point(0, GUI.IntScale(314)),
                MinSize = new Point(0, GUI.IntScale(18))
            },
            barSize: 1.0f,
            isHorizontal: true)
        {
            Range = new Vector2(0.0f, 1.0f),
            Visible = false,
            Enabled = false
        };
        horizontalScrollBar.OnMoved = (scrollBar, _) =>
        {
            if (list != null)
            {
                spriteHorizontalScrollOffsets[list] = scrollBar.BarScrollValue;
            }
            return true;
        };

        list = new GUIListBox(
            new RectTransform(new Vector2(1.0f, 0.0f), panel.RectTransform, Anchor.TopLeft, Pivot.TopLeft)
            {
                AbsoluteOffset = new Point(0, GUI.IntScale(34))
            },
            style: null);
        list.Padding = Vector4.Zero;
        list.RectTransform.MinSize = new Point(0, GUI.IntScale(276));
        list.OnAddedToGUIUpdateList = component =>
        {
            if (component is GUIListBox listBox && spriteListsPendingScrollReset.Remove(listBox))
            {
                ResetSpriteListScroll(listBox);
            }
            UpdateSpriteHorizontalScrollBar(list);
        };
        spriteHorizontalScrollBars[list] = horizontalScrollBar;
        spriteHorizontalScrollOffsets[list] = 0.0f;
        return list;
    }

    private void UpdateAllViewerSpriteInfo()
    {
        UpdateBodySpriteInfo();
        UpdateHeadSpriteInfo();
        UpdateClothingSpriteInfo();
    }

    private void UpdateBodySpriteInfo()
    {
        if (bodySpriteInfoList == null) { return; }

        var entries = new List<ViewerSpriteEntry>();
        IEnumerable<Limb> limbs = CurrentCharacter?.AnimController?.Limbs ?? Array.Empty<Limb>();
        foreach (Limb limb in limbs)
        {
            if (limb?.ActiveSprite == null || limb.type == LimbType.Head) { continue; }
            entries.Add(CreateSpriteEntry(limb.type.ToString(), string.Empty, limb.ActiveSprite));
        }
        PopulateSpritePreviewList(bodySpriteInfoList, entries, bodySpritePreviewZoom, "No body sprites available.");
    }

    private void UpdateHeadSpriteInfo()
    {
        if (headSpriteInfoList == null) { return; }

        var entries = new List<ViewerSpriteEntry>();
        Character character = CurrentCharacter;
        if (character?.Info?.HeadSprite != null)
        {
            entries.Add(CreateSpriteEntry("Head", $"tags {string.Join(", ", character.Info.Head.Preset.TagSet.Select(static tag => tag.Value))}", character.Info.HeadSprite));
        }

        Limb head = character?.AnimController?.GetLimb(LimbType.Head);
        if (head != null)
        {
            foreach (WearableSprite wearableSprite in head.OtherWearables.Where(static w => w?.Sprite != null))
            {
                entries.Add(CreateSpriteEntry($"{wearableSprite.Type} / Head", wearableSprite.Limb.ToString(), wearableSprite, head, character));
            }
        }

        PopulateSpritePreviewList(headSpriteInfoList, entries, headSpritePreviewZoom, "No head sprites available.");
    }

    private void UpdateClothingSpriteInfo()
    {
        if (clothingSpriteInfoList == null) { return; }
        List<ViewerSpriteEntry> entries = GetSelectedClothingSpriteEntries();
        PopulateClothingSpriteSelectionList(entries);
        RefreshSelectedSpriteEditor(entries);
        PopulateClothingSpritePreview(entries);
    }

    private void PopulateClothingSpritePreview(IReadOnlyList<ViewerSpriteEntry> entries)
    {
        PopulateSpritePreviewList(
            clothingSpriteInfoList,
            entries,
            clothingSpritePreviewZoom,
            selectedClothingPrefab == null ? "No clothing selected." : "Selected clothing has no visible sprites.");
    }

    private List<ViewerSpriteEntry> GetSelectedClothingSpriteEntries()
    {
        return CurrentCharacter?.AnimController?.Limbs
            .Where(static limb => limb != null)
            .SelectMany(static limb => limb.WearingItems.Select(sprite => (limb, sprite)))
            .Where(tuple => tuple.sprite?.WearableComponent?.Item?.Prefab == selectedClothingPrefab)
            .Distinct()
            .Where(static tuple => tuple.sprite?.Sprite != null)
            .Select(tuple => CreateSpriteEntry($"{tuple.sprite.WearableComponent.Item.Prefab.Name} {tuple.sprite.Limb}", tuple.sprite.Type.ToString(), tuple.sprite, tuple.limb, CurrentCharacter))
            .ToList() ?? new List<ViewerSpriteEntry>();
    }

    private ViewerSpriteEntry CreateSpriteEntry(string title, string subtitle, WearableSprite wearableSprite, Limb limb, Character character)
    {
        ViewerSpriteEntry entry = CreateSpriteEntry(title, subtitle, wearableSprite.Sprite, wearableSprite.Scale);
        entry.InheritSourceRect = wearableSprite.InheritSourceRect;
        entry.InheritOrigin = wearableSprite.InheritOrigin;
        entry.SourceElement = wearableSprite.SourceElement;
        entry.WearableSprite = wearableSprite;
        entry.IsEditable = wearableSprite.SourceElement?.Element != null;
        ResolveInheritedSpriteValues(entry, wearableSprite, limb, character);
        ApplyStoredSpriteEdits(entry);
        return entry;
    }

    private static ViewerSpriteEntry CreateSpriteEntry(string title, string subtitle, Sprite sprite, float scale = 1.0f)
    {
        return new ViewerSpriteEntry
        {
            Title = title,
            Subtitle = subtitle,
            Sprite = sprite,
            SourceRect = sprite.SourceRect,
            Origin = sprite.Origin,
            Scale = scale,
            FilePath = sprite.FilePath.Value,
            SourceElement = sprite.SourceElement,
            IsEditable = false
        };
    }

    private void ApplyStoredSpriteEdits(ViewerSpriteEntry entry)
    {
        XElement element = entry.SourceElement?.Element;
        if (element == null) { return; }

        liveSpritesByElement[element] = entry.Sprite;
        if (editedSourceRects.TryGetValue(element, out Rectangle sourceRect))
        {
            entry.SourceRect = sourceRect;
            entry.Sprite.SourceRect = sourceRect;
            entry.InheritSourceRect = false;
        }
        if (editedOrigins.TryGetValue(element, out Vector2 origin))
        {
            entry.Origin = new Vector2(origin.X * entry.SourceRect.Width, origin.Y * entry.SourceRect.Height);
            entry.Sprite.RelativeOrigin = origin;
            entry.InheritOrigin = false;
        }
        entry.IsDirty = dirtySpriteElements.Contains(element);
        sourcePackagesByElement[element] = entry.SourceElement.ContentPackage;
    }

    private void PopulateClothingSpriteSelectionList(IReadOnlyList<ViewerSpriteEntry> entries)
    {
        if (clothingSpriteSelectionList == null) { return; }

        clothingSpriteSelectionList.ClearChildren();
        foreach (ViewerSpriteEntry entry in entries.Where(static e => e.IsEditable))
        {
            XElement element = entry.SourceElement.Element;
            var row = new GUITextBlock(
                new RectTransform(new Point(0, GUI.IntScale(22)), clothingSpriteSelectionList.Content.RectTransform),
                $"{(dirtySpriteElements.Contains(element) ? "* " : string.Empty)}{entry.Title} / {entry.Subtitle}",
                font: GUIStyle.SmallFont,
                textAlignment: Alignment.CenterLeft)
            {
                UserData = entry,
                ToolTip = entry.Tooltip,
                TextColor = element == selectedSpriteElement ? GUIStyle.Green : GUIStyle.TextColorNormal
            };
        }

        if (entries.None(static e => e.IsEditable))
        {
            new GUITextBlock(
                new RectTransform(new Point(0, GUI.IntScale(22)), clothingSpriteSelectionList.Content.RectTransform),
                selectedClothingPrefab == null ? "No clothing selected." : "No editable clothing sprites.",
                font: GUIStyle.SmallFont)
            {
                CanBeFocused = false
            };
        }
        clothingSpriteSelectionList.UpdateScrollBarSize();
    }

    private void SelectSpriteEntry(ViewerSpriteEntry entry)
    {
        if (entry?.SourceElement?.Element == null || !entry.IsEditable) { return; }
        selectedSpriteElement = entry.SourceElement.Element;
        RefreshSelectedSpriteEditor(GetSelectedClothingSpriteEntries());
        PopulateClothingSpriteSelectionList(GetSelectedClothingSpriteEntries());
        selectedSpriteStatusText.Text = dirtySpriteElements.Contains(selectedSpriteElement) ? "Selected sprite has unsaved changes." : "";
    }

    private void RefreshSelectedSpriteEditor(IReadOnlyList<ViewerSpriteEntry> entries)
    {
        if (selectedSpriteTitleText == null) { return; }

        ViewerSpriteEntry selected = selectedSpriteElement == null ? null : entries.FirstOrDefault(e => e.SourceElement?.Element == selectedSpriteElement);
        bool hasSelection = selected != null;
        selectedSpriteTitleText.Text = hasSelection
            ? $"{selected.Title} / {selected.Subtitle}"
            : "No clothing sprite selected.";

        suppressSpriteEditorEvents = true;
        try
        {
            foreach (GUINumberInput input in GetSpriteEditorInputs())
            {
                if (input == null) { continue; }
                input.Enabled = hasSelection;
            }

            if (hasSelection)
            {
                Rectangle source = selected.SourceRect;
                Vector2 relativeOrigin = selected.Sprite.RelativeOrigin;
                sourceXInput.IntValue = source.X;
                sourceYInput.IntValue = source.Y;
                sourceWidthInput.IntValue = source.Width;
                sourceHeightInput.IntValue = source.Height;
                originXInput.FloatValue = relativeOrigin.X;
                originYInput.FloatValue = relativeOrigin.Y;
            }
        }
        finally
        {
            suppressSpriteEditorEvents = false;
        }
    }

    private IEnumerable<GUINumberInput> GetSpriteEditorInputs()
    {
        yield return sourceXInput;
        yield return sourceYInput;
        yield return sourceWidthInput;
        yield return sourceHeightInput;
        yield return originXInput;
        yield return originYInput;
    }

    private void ApplySpriteEditorInputs()
    {
        if (suppressSpriteEditorEvents || selectedSpriteElement == null) { return; }
        if (!liveSpritesByElement.TryGetValue(selectedSpriteElement, out Sprite sprite) || sprite == null) { return; }

        Rectangle sourceRect = new Rectangle(
            sourceXInput.IntValue,
            sourceYInput.IntValue,
            Math.Max(1, sourceWidthInput.IntValue),
            Math.Max(1, sourceHeightInput.IntValue));
        Vector2 relativeOrigin = new Vector2(originXInput.FloatValue, originYInput.FloatValue);

        sprite.SourceRect = sourceRect;
        sprite.RelativeOrigin = relativeOrigin;
        editedSourceRects[selectedSpriteElement] = sourceRect;
        editedOrigins[selectedSpriteElement] = relativeOrigin;
        dirtySpriteElements.Add(selectedSpriteElement);
        materializedSourceRectElements.Add(selectedSpriteElement);
        materializedOriginElements.Add(selectedSpriteElement);

        selectedSpriteStatusText.Text = "Unsaved changes.";
        List<ViewerSpriteEntry> entries = GetSelectedClothingSpriteEntries();
        PopulateClothingSpriteSelectionList(entries);
        PopulateClothingSpritePreview(entries);
    }

    private void RevertSelectedSpriteEdits()
    {
        if (selectedSpriteElement == null) { return; }
        editedSourceRects.Remove(selectedSpriteElement);
        editedOrigins.Remove(selectedSpriteElement);
        dirtySpriteElements.Remove(selectedSpriteElement);
        materializedSourceRectElements.Remove(selectedSpriteElement);
        materializedOriginElements.Remove(selectedSpriteElement);

        selectedSpriteStatusText.Text = "Reverted selected sprite edits.";
        EquipViewerClothing(selectedClothingPrefab);
    }

    private void SaveDirtySpriteXml()
    {
        if (dirtySpriteElements.None())
        {
            selectedSpriteStatusText.Text = "No sprite changes to save.";
            return;
        }

        var docsToSave = new HashSet<XDocument>();
        var blockedFiles = new HashSet<string>();
        foreach (XElement element in dirtySpriteElements.ToArray())
        {
            XDocument doc = element.Document;
            if (doc == null) { continue; }
            string xmlPath = doc.ParseContentPathFromUri();
            sourcePackagesByElement.TryGetValue(element, out ContentPackage package);
            if (!CanSaveLocalXml(package, xmlPath, out string reason))
            {
                blockedFiles.Add($"{Path.GetFileName(xmlPath)}: {reason}");
                continue;
            }

            if (editedSourceRects.TryGetValue(element, out Rectangle sourceRect))
            {
                element.SetAttributeValue("sourcerect", XMLExtensions.RectToString(sourceRect));
                if (materializedSourceRectElements.Contains(element))
                {
                    element.SetAttributeValue("inheritsourcerect", false);
                }
            }
            if (editedOrigins.TryGetValue(element, out Vector2 origin))
            {
                element.SetAttributeValue("origin", XMLExtensions.Vector2ToString(origin));
                if (materializedOriginElements.Contains(element))
                {
                    element.SetAttributeValue("inheritorigin", false);
                }
            }
            docsToSave.Add(doc);
        }

        int savedCount = 0;
        var savedDocs = new HashSet<XDocument>();
        foreach (XDocument doc in docsToSave)
        {
            string xmlPath = doc.ParseContentPathFromUri();
            try
            {
#if DEBUG
                doc.Save(xmlPath);
#else
                Barotrauma.IO.SafeXML.SaveSafe(doc, xmlPath, throwExceptions: true);
#endif
                savedCount++;
                savedDocs.Add(doc);
            }
            catch (Exception ex)
            {
                blockedFiles.Add($"{Path.GetFileName(xmlPath)}: {ex.Message}");
            }
        }

        foreach (XElement element in dirtySpriteElements.ToArray())
        {
            if (element.Document != null && savedDocs.Contains(element.Document))
            {
                dirtySpriteElements.Remove(element);
                materializedSourceRectElements.Remove(element);
                materializedOriginElements.Remove(element);
            }
        }

        selectedSpriteStatusText.Text = savedCount > 0
            ? $"Saved {savedCount} XML file{(savedCount == 1 ? string.Empty : "s")}."
            : "No XML files saved.";
        if (blockedFiles.Any())
        {
            selectedSpriteStatusText.Text += "\nBlocked: " + string.Join("; ", blockedFiles);
        }
        UpdateClothingSpriteInfo();
    }

    private static bool CanSaveLocalXml(ContentPackage package, string xmlPath, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(xmlPath) || !File.Exists(xmlPath))
        {
            reason = "source XML missing";
            return false;
        }
        if (!xmlPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            reason = "not an XML file";
            return false;
        }
        if (package == null || !ContentPackageManager.LocalPackages.Contains(package))
        {
            reason = "not a local mod file";
            return false;
        }
        string fullPath = Path.GetFullPath(xmlPath).CleanUpPathCrossPlatform(correctFilenameCase: false);
        string localDir = Path.GetFullPath(package.Dir).CleanUpPathCrossPlatform(correctFilenameCase: false);
        if (!fullPath.StartsWith(localDir, StringComparison.OrdinalIgnoreCase))
        {
            reason = "outside local mod directory";
            return false;
        }
        try
        {
            using FileStream stream = File.Open(xmlPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        }
        catch
        {
            reason = "file is read-only or locked";
            return false;
        }
        return true;
    }

    private void PopulateSpritePreviewList(GUIListBox list, IReadOnlyList<ViewerSpriteEntry> entries, float zoom, string emptyText)
    {
        list.ClearChildren();
        if (entries.None())
        {
            new GUITextBlock(new RectTransform(new Point(0, GUI.IntScale(24)), list.Content.RectTransform, Anchor.TopLeft, Pivot.TopLeft), emptyText, font: GUIStyle.SmallFont)
            {
                CanBeFocused = false
            };
            spriteCanvasWidths[list] = 0;
            spriteHorizontalScrollOffsets[list] = 0.0f;
            UpdateSpriteHorizontalScrollBar(list);
            RequestSpriteListScrollReset(list);
            return;
        }

        int padding = GUI.IntScale(4);
        int fileLabelHeight = GUI.IntScale(16);
        int y = 0;
        int width = list.Rect.Width - GUI.IntScale(28);
        var groups = entries.GroupBy(static entry => entry.FilePath).ToList();
        foreach (var group in groups)
        {
            int textureWidth = group.First().Sprite.Texture?.Width ?? group.Max(static entry => entry.SourceRect.Right);
            int textureHeight = group.First().Sprite.Texture?.Height ?? group.Max(static entry => entry.SourceRect.Bottom);
            width = Math.Max(width, padding * 2 + (int)(textureWidth * zoom));
            y += fileLabelHeight + (int)(textureHeight * zoom) + padding;
        }

        new GUICustomComponent(
            new RectTransform(new Point(width, Math.Max(y, GUI.IntScale(24))), list.Content.RectTransform, Anchor.TopLeft, Pivot.TopLeft),
            onDraw: (spriteBatch, component) => DrawSpritePreviewCanvas(spriteBatch, component, list, entries, zoom, GetSpriteHorizontalScrollOffset(list)))
        {
            CanBeFocused = true,
            HideElementsOutsideFrame = false
        };
        spriteCanvasWidths[list] = width;
        UpdateSpriteHorizontalScrollBar(list);
        RequestSpriteListScrollReset(list);
    }

    private void RequestSpriteListScrollReset(GUIListBox list)
    {
        ResetSpriteListScroll(list);
        spriteListsPendingScrollReset.Add(list);
    }

    private static void ResetSpriteListScroll(GUIListBox list)
    {
        list.UpdateScrollBarSize();
        list.BarScroll = 0.0f;
        list.ScrollBar.BarScroll = 0.0f;
        list.RecalculateChildren();
    }

    private float GetSpriteHorizontalScrollOffset(GUIListBox list)
    {
        return spriteHorizontalScrollOffsets.TryGetValue(list, out float offset) ? offset : 0.0f;
    }

    private void UpdateSpriteHorizontalScrollBar(GUIListBox list)
    {
        if (list == null || !spriteHorizontalScrollBars.TryGetValue(list, out GUIScrollBar scrollBar)) { return; }

        int viewportWidth = Math.Max(1, list.Content.Rect.Width);
        int canvasWidth = spriteCanvasWidths.TryGetValue(list, out int storedWidth) ? storedWidth : 0;
        int maxOffset = Math.Max(0, canvasWidth - viewportWidth);
        bool needsHorizontalScroll = maxOffset > GUI.IntScale(2);

        scrollBar.Visible = needsHorizontalScroll;
        scrollBar.Enabled = needsHorizontalScroll;
        scrollBar.Range = new Vector2(0.0f, Math.Max(1.0f, maxOffset));
        scrollBar.BarSize = needsHorizontalScroll ? MathHelper.Clamp(viewportWidth / (float)Math.Max(viewportWidth, canvasWidth), 0.05f, 1.0f) : 1.0f;

        float offset = spriteHorizontalScrollOffsets.TryGetValue(list, out float storedOffset) ? storedOffset : 0.0f;
        offset = MathHelper.Clamp(offset, 0.0f, maxOffset);
        spriteHorizontalScrollOffsets[list] = offset;
        scrollBar.BarScrollValue = needsHorizontalScroll ? offset : 0.0f;
    }

    private void DrawSpritePreviewCanvas(SpriteBatch spriteBatch, GUICustomComponent component, GUIListBox list, IReadOnlyList<ViewerSpriteEntry> entries, float zoom, float horizontalOffset)
    {
        int padding = GUI.IntScale(8);
        int fileLabelHeight = GUI.IntScale(16);
        int y = component.Rect.Y;
        int x = component.Rect.X - (int)horizontalOffset;
        Point mousePos = PlayerInput.MousePosition.ToPoint();
        string tooltip = null;
        ViewerSpriteEntry clickedEntry = null;

        foreach (var group in entries.GroupBy(static entry => entry.FilePath))
        {
            ViewerSpriteEntry first = group.First();
            Texture2D texture = first.Sprite.Texture;
            if (texture == null) { continue; }

            string fileName = Path.GetFileName(first.FilePath);
            GUI.DrawString(spriteBatch, new Vector2(x + padding, y), fileName, GUIStyle.TextColorNormal, font: GUIStyle.SmallFont);
            y += fileLabelHeight;

            Rectangle sheetRect = new Rectangle(x + padding, y, (int)(texture.Width * zoom), (int)(texture.Height * zoom));
            spriteBatch.Draw(texture, sheetRect, Color.White);
            GUI.DrawRectangle(spriteBatch, sheetRect, GUIStyle.TextColorDim, isFilled: false);

            foreach (ViewerSpriteEntry entry in group)
            {
                Rectangle source = entry.SourceRect;
                Rectangle dest = new Rectangle(
                    sheetRect.X + (int)(source.X * zoom),
                    sheetRect.Y + (int)(source.Y * zoom),
                    Math.Max(1, (int)(source.Width * zoom)),
                    Math.Max(1, (int)(source.Height * zoom)));
                Color outline = dest.Contains(mousePos) ? Color.Yellow : Color.Red;
                if (entry.SourceElement?.Element == selectedSpriteElement)
                {
                    outline = Color.Lime;
                }
                GUI.DrawRectangle(spriteBatch, dest, outline, isFilled: false, thickness: dest.Contains(mousePos) ? 2 : 1);
                if (dest.Contains(mousePos))
                {
                    tooltip = entry.Tooltip;
                    if (list == clothingSpriteInfoList && entry.IsEditable && PlayerInput.DoubleClicked())
                    {
                        clickedEntry = entry;
                    }
                }
            }

            y += sheetRect.Height + padding;
        }

        component.ToolTip = string.Empty;
        if (!string.IsNullOrEmpty(tooltip))
        {
            GUIComponent.DrawToolTip(spriteBatch, tooltip, PlayerInput.MousePosition + new Vector2(GUI.IntScale(20), GUI.IntScale(20)));
        }
        if (clickedEntry != null)
        {
            SelectSpriteEntry(clickedEntry);
        }
    }

    private static void ResolveInheritedSpriteValues(ViewerSpriteEntry entry, WearableSprite wearableSprite, Limb limb, Character character)
    {
        Sprite activeSprite = limb?.ActiveSprite;
        if (activeSprite == null) { return; }

        if (wearableSprite.InheritSourceRect)
        {
            if (wearableSprite.SheetIndex.HasValue)
            {
                entry.SourceRect = new Rectangle(CharacterInfo.CalculateOffset(activeSprite, wearableSprite.SheetIndex.Value), activeSprite.SourceRect.Size);
            }
            else if (limb.type == LimbType.Head && character?.Info?.Head != null)
            {
                entry.SourceRect = new Rectangle(CharacterInfo.CalculateOffset(activeSprite, character.Info.Head.SheetIndex.ToPoint()), activeSprite.SourceRect.Size);
            }
            else
            {
                entry.SourceRect = activeSprite.SourceRect;
            }
        }

        if (wearableSprite.InheritOrigin)
        {
            entry.Origin = activeSprite.Origin;
        }
    }
}
