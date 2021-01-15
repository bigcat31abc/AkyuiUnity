using System;
using System.Collections.Generic;
using System.Linq;
using AkyuiUnity.Editor;
using AkyuiUnity.Loader;
using Unity.VectorGraphics;
using Unity.VectorGraphics.Editor;
using UnityEditor;
using UnityEngine;
using XdParser;
using XdParser.Internal;
using Random = UnityEngine.Random;

namespace AkyuiUnity.Xd
{
    public static class XdImporter
    {
        public static void Import(XdImportSettings settings, string[] xdFilePaths)
        {
            var loaders = xdFilePaths
                .Select(x => new XdFile(x))
                .SelectMany(x => x.Artworks.Select((y => (x, y))))
                .Where(x => x.y.Name != "pasteboard")
                .Select(x => (IAkyuiLoader) new XdAkyuiLoader(x.x, x.y))
                .ToArray();
            Importer.Import(settings, loaders);
            foreach (var loader in loaders) loader.Dispose();
        }
    }

    public class XdAkyuiLoader : IAkyuiLoader
    {
        private XdFile _xdFile;
        private readonly Dictionary<string, XdStyleFillPatternMetaJson> _fileNameToMeta;
        private readonly Dictionary<string, byte[]> _fileNameToBytes;

        public XdAkyuiLoader(XdFile xdFile, XdArtboard xdArtboard)
        {
            _xdFile = xdFile;
            (LayoutInfo, AssetsInfo, _fileNameToMeta, _fileNameToBytes) = Create(xdArtboard);
        }

        public void Dispose()
        {
            _xdFile?.Dispose();
            _xdFile = null;
        }

        public LayoutInfo LayoutInfo { get; }
        public AssetsInfo AssetsInfo { get; }

        public byte[] LoadAsset(string fileName)
        {
            if (_fileNameToMeta.ContainsKey(fileName))
            {
                var meta = _fileNameToMeta[fileName];
                return _xdFile.GetResource(meta);
            }

            if (_fileNameToBytes.ContainsKey(fileName))
            {
                return _fileNameToBytes[fileName];
            }

            throw new Exception($"Unknown asset {fileName}");
        }

        private (LayoutInfo, AssetsInfo, Dictionary<string, XdStyleFillPatternMetaJson>, Dictionary<string, byte[]>) Create(XdArtboard xdArtboard)
        {
            var renderer = new XdRenderer(xdArtboard);
            var layoutInfo = new LayoutInfo(
                renderer.Name,
                renderer.Hash,
                renderer.Meta,
                renderer.Root,
                renderer.Elements.ToArray()
            );
            var assetsInfo = new AssetsInfo(
                renderer.Assets.ToArray()
            );
            return (layoutInfo, assetsInfo, renderer.FileNameToMeta, renderer.FileNameToBytes);
        }

        private class XdRenderer
        {
            public string Name { get; }
            public long Hash => Random.Range(0, 100000); // 今は必ず更新する
            public Meta Meta => new Meta(Const.AkyuiVersion, "XdToAkyui", "0.0.0");
            public int Root => 0;
            public List<IElement> Elements { get; }
            public List<IAsset> Assets { get; }
            public Dictionary<string, XdStyleFillPatternMetaJson> FileNameToMeta { get; }
            public Dictionary<string, byte[]> FileNameToBytes { get; }

            private int _nextEid = 1;
            private Dictionary<string, Rect> _size;

            public XdRenderer(XdArtboard xdArtboard)
            {
                var resources = xdArtboard.Resources;

                Name = xdArtboard.Name;
                Elements = new List<IElement>();
                Assets = new List<IAsset>();
                FileNameToMeta = new Dictionary<string, XdStyleFillPatternMetaJson>();
                FileNameToBytes = new Dictionary<string, byte[]>();
                _size = new Dictionary<string, Rect>();

                var xdResourcesArtboardsJson = resources.Artboards[xdArtboard.Manifest.Path.Replace("artboard-", "")];
                var rootSize = new Vector2(xdResourcesArtboardsJson.Width, xdResourcesArtboardsJson.Height);
                CalcPosition(xdArtboard.Artboard.Children.SelectMany(x => x.Artboard.Children).ToArray(), rootSize);
                var children = Render(xdArtboard.Artboard.Children.SelectMany(x => x.Artboard.Children).ToArray());
                var root = new ObjectElement(
                    0,
                    xdArtboard.Name,
                    Vector2.zero,
                    rootSize,
                    AnchorXType.Center,
                    AnchorYType.Middle,
                    new IComponent[] { },
                    children.Select(x => x.Eid).ToArray()
                );
                Elements.Add(root);
            }

            private string[] CalcPosition(XdObjectJson[] xdObjects, Vector2 rootSize)
            {
                return xdObjects.Select(xdObject => CalcPosition(xdObject, rootSize)).ToArray();
            }

            private string CalcPosition(XdObjectJson xdObject, Vector2 rootSize)
            {
                var children = new string[] { };
                if (xdObject.Group != null)
                {
                    children = CalcPosition(xdObject.Group.Children, rootSize);
                }

                if (xdObject.Type == "shape")
                {
                    var size = new Vector2(xdObject.Shape.Width, xdObject.Shape.Height);
                    var position = new Vector2(xdObject.Transform.Tx, xdObject.Transform.Ty) - rootSize / 2f;
                    _size[xdObject.Id] = new Rect(position, size);
                    return xdObject.Id;
                }

                if (xdObject.Type == "group")
                {
                    var childrenRects = children.Select(x => _size[x]).ToArray();
                    var minX = childrenRects.Length > 0 ? childrenRects.Min(x => x.min.x) : 0f;
                    var minY = childrenRects.Length > 0 ? childrenRects.Min(x => x.min.y) : 0f;
                    var maxX = childrenRects.Length > 0 ? childrenRects.Max(x => x.max.x) : 0f;
                    var maxY = childrenRects.Length > 0 ? childrenRects.Max(x => x.max.y) : 0f;
                    var groupRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
                    _size[xdObject.Id] = groupRect;
                    foreach (var c in children)
                    {
                        var t = _size[c];
                        t.center = groupRect.center - t.center;
                        _size[c] = t;
                    }
                    return xdObject.Id;
                }

                throw new Exception($"Unknown object type {xdObject.Type}");
            }

            private IElement[] Render(XdObjectJson[] xdObjects)
            {
                var children = new List<IElement>();
                foreach (var xdObject in xdObjects) children.AddRange(Render(xdObject));
                return children.ToArray();
            }

            private IElement[] Render(XdObjectJson xdObject)
            {
                var eid = _nextEid;
                _nextEid++;

                var rect = _size[xdObject.Id];
                var position = new Vector2(rect.center.x, -rect.center.y);
                var size = rect.size;
                var children = new IElement[] { };
                if (xdObject.Group != null)
                {
                    children = Render(xdObject.Group.Children);
                }

                var anchorX = AnchorXType.Center;
                var anchorY = AnchorYType.Middle;
                var constRight = xdObject.Meta.Ux.ConstraintRight;
                var constLeft = xdObject.Meta.Ux.ConstraintLeft;
                if (constRight && constLeft) anchorX = AnchorXType.Stretch;
                else if (constRight) anchorX = AnchorXType.Right;
                else if (constLeft) anchorX = AnchorXType.Left;

                var constTop = xdObject.Meta.Ux.ConstraintTop;
                var constBottom = xdObject.Meta.Ux.ConstraintBottom;
                if (constTop && constBottom) anchorY = AnchorYType.Stretch;
                else if (constTop) anchorY = AnchorYType.Top;
                else if (constBottom) anchorY = AnchorYType.Bottom;

                if (xdObject.Type == "shape")
                {
                    var components = new List<IComponent>();

                    var spriteUid = xdObject.Style.Fill.Pattern?.Meta?.Ux?.Uid;
                    if (!string.IsNullOrWhiteSpace(spriteUid))
                    {
                        spriteUid = $"{spriteUid}.png";
                        Assets.Add(new SpriteAsset(spriteUid, Random.Range(0, 10000)));
                        components.Add(new ImageComponent(
                            0,
                            spriteUid,
                            Color.white
                        ));
                        FileNameToMeta[spriteUid] = xdObject.Style.Fill.Pattern.Meta;
                    }

                    var shapeType = xdObject.Shape?.Type;
                    if (shapeType == "path")
                    {
                        spriteUid = $"path_{xdObject.Id}.svg";
                        Assets.Add(new SpriteAsset(spriteUid, Random.Range(0, 10000)));
                        components.Add(new ImageComponent(
                            0,
                            spriteUid,
                            Color.white
                        ));

                        var svgArgs = new List<string>();
                        var fill = xdObject.Style?.Fill;
                        if (fill != null)
                        {
                            var color = new Color32((byte) fill.Color.Value.R, (byte) fill.Color.Value.G, (byte) fill.Color.Value.B, 255);
                            svgArgs.Add($@"fill=""#{ColorUtility.ToHtmlStringRGB(color)}""");
                        }
                        var svg = $@"<svg><path d=""{xdObject.Shape.Path}"" {string.Join(" ", svgArgs)} /></svg>";
                        FileNameToBytes[spriteUid] = System.Text.Encoding.UTF8.GetBytes(svg);
                    }

                    var sprite = new ObjectElement(
                        eid,
                        xdObject.Name,
                        position,
                        size,
                        anchorX,
                        anchorY,
                        components.ToArray(),
                        children.Select(x => x.Eid).ToArray()
                    );

                    Elements.Add(sprite);
                    return new IElement[] { sprite };
                }

                if (xdObject.Type == "group")
                {
                    var group = new ObjectElement(
                        eid,
                        xdObject.Name,
                        position,
                        size,
                        anchorX,
                        anchorY,
                        new IComponent[] { },
                        children.Select(x => x.Eid).ToArray()
                    );

                    Elements.Add(group);
                    return new IElement[] { group };
                }

                throw new Exception($"Unknown object type {xdObject.Type}");
            }
        }
    }

    public class SvgPostProcessImportAsset : AssetPostprocessor
    {
        public void OnPreprocessAsset()
        {
            if (PostProcessImportAsset.ProcessingFile != assetPath) return;

            if (assetImporter is SVGImporter svgImporter)
            {
                svgImporter.SvgType = SVGType.TexturedSprite;
            }
        }
    }
}