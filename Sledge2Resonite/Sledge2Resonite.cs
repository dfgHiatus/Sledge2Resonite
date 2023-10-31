using Elements.Core;
using Elements.Assets;
using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;
using Sledge.Formats.Valve;
using Sledge.Formats.Texture.Vtf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using File = System.IO.File;
using FrooxEngine.Undo;
using TextureFormat = Elements.Assets.TextureFormat;

namespace Sledge2Resonite;

public class Sledge2Resonite : ResoniteMod
{
    public override string Name => "Sledge2Resonite";
    public override string Author => "Elektrospy and dfgHiatus";
    public override string Version => "0.1.1";
    public override string Link => "https://github.com/Elektrospy/Sledge2Resonite";

    internal static ModConfiguration config;

    #region ModConfigurationKeys

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> importTextureRow 
        = new("textureRows", "Import Textures number of rows", () => 5);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> tintSpecular 
        = new("tintSpecular", "Tint Specular Textures on Import", () => false);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> generateTextureAtlas 
        = new("Generate frame atlas", "Auto generate atlas of multiframe textures", () => true);

    [AutoRegisterConfigKey]
    internal static ModConfigurationKey<bool> SSBumpAutoConvert 
        = new("SSBump auto convert", "Auto convert SSBump to NormalMap", () => true);

    [AutoRegisterConfigKey]
    internal static ModConfigurationKey<bool> invertNormalmapG 
        = new("Invert normal map G ", "Invert the green color channel of normal maps", () => true);

    #endregion

    internal static Dictionary<string, VtfFile> vtfDictionary = new Dictionary<string, VtfFile>();
    internal static Dictionary<string, SerialisedObject> vmtDictionary = new Dictionary<string, SerialisedObject>();

    private static readonly HashSet<string> propertyTextureNamesHashSet = new HashSet<string>()
    {
        "$basetexture", 
        "$detail", 
        "$normalmap", 
        "$bumpmap", 
        "$heightmap", 
        "$envmapmask", 
        "$selfillumtexture", 
        "$selfillummask"
    };

    public override void OnEngineInit()
    {
        new Harmony("net.Elektrospy.Sledge2Resonite").PatchAll();
        config = GetConfiguration();
        Engine.Current.RunPostInit(AssetPatch);
    }

    private static void AssetPatch()
    {
        var aExt = Traverse.Create(typeof(AssetHelper)).Field<Dictionary<AssetClass, List<string>>>("associatedExtensions");
        aExt.Value[AssetClass.Special].Add("vtf");
        aExt.Value[AssetClass.Special].Add("vmt");
        aExt.Value[AssetClass.Special].Add("raw");
    }

    [HarmonyPatch(typeof(UniversalImporter), "ImportTask", typeof(AssetClass), typeof(IEnumerable<string>), typeof(World), typeof(float3), typeof(floatQ), typeof(float3), typeof(bool))]
    public static class UniversalImporterPatch
    {
        static bool Prefix(ref IEnumerable<string> files, ref Task __result, World world)
        {
            var query = files.Where(x =>
                x.EndsWith("vtf", StringComparison.InvariantCultureIgnoreCase) ||
                x.EndsWith("vmt", StringComparison.InvariantCultureIgnoreCase) ||
                x.EndsWith("raw", StringComparison.InvariantCultureIgnoreCase));

            if (query.Any())
                __result = ProcessSledgeImport(query, world);

            return true;
        }
    }

    private static async Task ProcessSledgeImport(IEnumerable<string> inputFiles, World world)
    {
        await default(ToBackground);
        await ParseInputFiles(inputFiles, world, true);
        ClearDictionaries();
        await default(ToWorld);
    }

    private static async Task ParseInputFiles(IEnumerable<string> inputFiles, World world, bool createQuads = false)
    {
        SerialisedObjectFormatter ValveSerialiser = new SerialisedObjectFormatter();
        string[] filesArr = inputFiles.ToArray();
        int vtfCounter = 0;
        int vmtCounter = 0;

        for (int i = 0; i < filesArr.Count(); ++i)
        {
            if (!File.Exists(filesArr[i])) continue;

            FileInfo currentFileInfo;
            FileStream fs;
            try
            {
                currentFileInfo = new FileInfo(filesArr[i]);
                fs = File.Open(filesArr[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception) { continue; }

            string currentFileName = currentFileInfo.Name.Split('.').First();
            string currentFileEnding = currentFileInfo.Extension;
            switch (currentFileEnding)
            {
                case ".vtf":
                    VtfFile tempVtf = new VtfFile(fs);
                    try
                    {
                        if (vtfDictionary.ContainsKey(currentFileName))
                            tempVtf = vtfDictionary[currentFileName];
                        else
                            vtfDictionary.Add(currentFileName, tempVtf);
                    }
                    catch (Exception) { }

                    if (!createQuads) return;

                    Slot currentSlot;
                    await default(ToWorld);
                    try
                    {
                        currentSlot = Engine.Current.WorldManager.FocusedWorld.AddSlot("Texture: " + currentFileName);
                        currentSlot.CreateSpawnUndoPoint();
                    }
                    catch (Exception) { continue; }
                    
                    currentSlot.PositionInFrontOfUser();
                    if (vtfCounter != 0)
                    {
                        float3 offset = UniversalImporter.GridOffset(ref vtfCounter, config.GetValue(importTextureRow));
                        currentSlot.GlobalPosition += offset;
                        vtfCounter++;
                    }
                    await CreateTextureQuadFromVtf(currentFileName, tempVtf, currentSlot);
                    break;
                case ".vmt":
                    VMTPreprocessor vmtPrePros = new VMTPreprocessor();
                    string fileLines;
                    try
                    {
                        fileLines = File.ReadAllText(filesArr[i]);
                    }
                    catch (Exception) { continue; }

                    vmtPrePros.ParseVmt(fileLines, out fileLines);
                    List<SerialisedObject> tempSerialzeObjectList = new();
                    using (MemoryStream memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(fileLines)))
                    {
                        try
                        {
                            tempSerialzeObjectList = ValveSerialiser.Deserialize(memoryStream).ToList();
                        }
                        catch (Exception) { continue; }
                    }

                    var firstVmtObject = tempSerialzeObjectList.First();

                    if (!vmtDictionary.ContainsKey(firstVmtObject.Name))
                        vmtDictionary.Add(currentFileName, firstVmtObject);

                    foreach (KeyValuePair<string, string> currentProperty in firstVmtObject.Properties)
                    {
                        if (!propertyTextureNamesHashSet.Contains(currentProperty.Key))
                            continue;
                        
                        string tempTexturePath = Utils.MergeTextureNameAndPath(currentProperty.Value, filesArr[i]);

                        if (!string.IsNullOrEmpty(tempTexturePath))
                            await ParseInputFiles(new string[] { tempTexturePath }, world);
                    }

                    if (vmtDictionary.ContainsKey(currentFileName))
                        await CreateMaterialOrbFromVmt(currentFileName, firstVmtObject);

                    vmtCounter++;
                    break;

                case ".raw":
                    Slot lutSlot;
                    await default(ToWorld);
                    try
                    {
                        lutSlot = Engine.Current.WorldManager.FocusedWorld.AddSlot("LUT: " + currentFileName);
                        lutSlot.CreateSpawnUndoPoint();
                    }
                    catch (Exception) { continue; }

                    lutSlot.PositionInFrontOfUser();
                    if (vtfCounter != 0)
                    {
                        float3 offset = UniversalImporter.GridOffset(ref vtfCounter, config.GetValue(importTextureRow));
                        lutSlot.GlobalPosition += offset;
                        vtfCounter++;
                    }
                    await NewLUTImport(filesArr[i], lutSlot);
                    break;
            }

            if (fs != null) fs.Dispose();
        }

        await default(ToWorld);
        await default(ToBackground);
    }

    private static async Task CreateMaterialOrbFromVmt(string currentVmtName, SerialisedObject currentSerialisedObject)
    {
        if (string.IsNullOrEmpty(currentVmtName))
            return;

        await default(ToBackground);
        VertexLitGenericParser vertexLitGenericParser = new VertexLitGenericParser();
        await vertexLitGenericParser.ParseMaterial(currentSerialisedObject.Properties, currentVmtName);
    }

    private static async Task CreateTextureQuadFromVtf(string currentVtfName, VtfFile currentVtf, Slot currentSlot)
    {   
        await default(ToWorld);
        currentSlot.PositionInFrontOfUser();
        VtfImage currentVtfImage = currentVtf.Images.GetLast();

        var newBitmap = new Bitmap2D(
            currentVtfImage.GetBgra32Data(),
            currentVtfImage.Width,
            currentVtfImage.Height,
            TextureFormat.BGRA32,
            false,
            ColorProfile.Linear,
            false);

        if (config.GetValue(generateTextureAtlas))
        {
            var imageList = currentVtf.Images;
            var mipmapNumber = currentVtf.Header.MipmapCount;
            var framesNumberRaw = imageList.Count;
            var framesNumber = framesNumberRaw / mipmapNumber;
            var bytesNumber = currentVtfImage.Width * currentVtfImage.Height * 4 * framesNumber;
            byte[] fillBytes = new byte[bytesNumber];

            var newAtlasBitmap = new Bitmap2D(
                fillBytes,
                currentVtfImage.Width * framesNumber,
                currentVtfImage.Height,
                TextureFormat.BGRA32,
                false,
                ColorProfile.Linear,
                false);

            var frameIndexStartOffset = imageList.Count - framesNumber;
            for (int currentFrame = frameIndexStartOffset; currentFrame < framesNumberRaw; currentFrame++)
            {
                try
                {
                    int currentOutputFrameIndex = currentFrame - frameIndexStartOffset;
                    var currentFrameImage = imageList[currentFrame];

                    var currentFrameBitmap = new Bitmap2D(
                        currentFrameImage.GetBgra32Data(),
                        currentFrameImage.Width,
                        currentFrameImage.Height,
                        TextureFormat.BGRA32,
                        false,
                        ColorProfile.Linear,
                        false);

                    for (int currentX = 0; currentX < currentFrameBitmap.Size.x; currentX++)
                    {
                        var pixelOffsetX = currentVtfImage.Width * currentOutputFrameIndex;
                        for (int currentY = 0; currentY < currentFrameBitmap.Size.y; currentY++)
                        {
                            var rawPixelColor = currentFrameBitmap.GetPixel(currentX, currentY);
                            newAtlasBitmap.SetPixel(currentX + pixelOffsetX, currentY, in rawPixelColor);
                        }
                    }
                }
                catch (Exception) { continue; }
            }
            newBitmap = newAtlasBitmap;
        }

        var currentUri = await currentSlot.World.Engine.LocalDB.SaveAssetAsync(newBitmap);
        StaticTexture2D currentTexture2D = currentSlot.AttachComponent<StaticTexture2D>();
        currentTexture2D.URL.Value = currentUri;

        if (currentVtf.Header.Flags.HasFlag(VtfImageFlag.Pointsample))
            currentTexture2D.FilterMode.Value = TextureFilterMode.Point;
        else if (currentVtf.Header.Flags.HasFlag(VtfImageFlag.Trilinear))
            currentTexture2D.FilterMode.Value = TextureFilterMode.Trilinear;
        else
        {
            currentTexture2D.FilterMode.Value = TextureFilterMode.Anisotropic;
            currentTexture2D.AnisotropicLevel.Value = 8;
        }

        if (currentVtf.Header.Flags.HasFlag(VtfImageFlag.Normal) &&
           (currentVtfName.ToLower().Contains("_normal") || currentVtfName.ToLower().Contains("_bump")))
        {
            currentTexture2D.IsNormalMap.Value = true;
            await currentTexture2D.ProcessPixels((color c) => new color(c.r, 1f - c.g, c.b, c.a));
        }

        if (currentVtf.Header.Flags.HasFlag(VtfImageFlag.Ssbump) &&
           (currentVtfName.ToLower().Contains("_bump") || currentVtfName.ToLower().Contains("_ssbump")) &&
           config.GetValue(SSBumpAutoConvert))
        {
            currentTexture2D.IsNormalMap.Value = true;
            Utils.SSBumpToNormal(currentTexture2D);
        }

        ImageImporter.SetupTextureProxyComponents(
            currentSlot,
            currentTexture2D,
            StereoLayout.None,
            ImageProjection.Perspective,
            false);
        ImageImporter.CreateQuad(
            currentSlot,
            currentTexture2D,
            StereoLayout.None,
            true);
        currentSlot.AttachComponent<Grabbable>().Scalable.Value = true;
    }

    private static async Task NewLUTImport(string path, Slot targetSlot)
    {
        await new ToBackground();
        const int sourceLUTWidth = 32;
        const int sourceLUTHeight = 1024;

        Bitmap2D rawBitmap2D = null;
        try
        {
            var rawBytes = File.ReadAllBytes(path);
            rawBitmap2D = new Bitmap2D(rawBytes, sourceLUTWidth, sourceLUTHeight, TextureFormat.RGB24, false, ColorProfile.Linear, false);
        }
        catch (Exception) { }

        if (rawBitmap2D.Size.x != sourceLUTWidth || rawBitmap2D.Size.y != sourceLUTHeight)
        {
            return;
        }

        const int pixelBoxSideLength = 32;
        var texture = new Bitmap3D(pixelBoxSideLength, pixelBoxSideLength, pixelBoxSideLength, TextureFormat.RGB24, false, ColorProfile.Linear);

        for (int currentBlockIndex = 0; currentBlockIndex < pixelBoxSideLength; currentBlockIndex++)
        {
            int rawYOffset = currentBlockIndex * pixelBoxSideLength;
            for (int rawY = 0; rawY < pixelBoxSideLength; rawY++)
            {
                for (int rawX = 0; rawX < pixelBoxSideLength; rawX++)
                {
                    color rawPixelColor = rawBitmap2D.GetPixel(rawX, rawY + rawYOffset);
                    texture.SetPixel(rawX, rawY, currentBlockIndex, in rawPixelColor);
                }
            }
        }

        Uri uriTexture2D = await targetSlot.Engine.LocalDB.SaveAssetAsync(rawBitmap2D).ConfigureAwait(false);
        Uri uriTexture3D = await targetSlot.Engine.LocalDB.SaveAssetAsync(texture).ConfigureAwait(false);

        await new ToWorld();

        var lutTexRaw = targetSlot.AttachComponent<StaticTexture2D>();
        lutTexRaw.URL.Value = uriTexture2D;
        lutTexRaw.FilterMode.Value = TextureFilterMode.Point;

        var lutTex = targetSlot.AttachComponent<StaticTexture3D>();
        lutTex.URL.Value = uriTexture3D;
        lutTex.FilterMode.Value = TextureFilterMode.Point;

        var lutMat = targetSlot.AttachComponent<LUT_Material>();
        lutMat.LUT.Target = lutTex;

        MaterialOrb.ConstructMaterialOrb(lutMat, targetSlot);
    }

    private static void ClearDictionaries()
    {
        vmtDictionary.Clear();
        vtfDictionary.Clear();
    }
}
