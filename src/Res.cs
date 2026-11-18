using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace MscTardis
{
    // Meshes, textures and sounds from Mods/Assets/MscTardis (a deploy copies assets/models, tex and sfx there, flat).
    // Unity objects die with the scene, so everything loads once per game scene: Init clears the caches.
    static class Res
    {
        static string dir;
        static readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
        static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
        static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();

        public static void Init(string assetsDir)
        {
            dir = assetsDir;
            meshes.Clear();
            textures.Clear();
            clips.Clear();
            cube = null;
            roomShaders.Clear();
        }


        public static bool Has(string file) { return File.Exists(Path.Combine(dir, file)); }

        // Placeholder geometry: everything is built from one unit cube, so a supplied model only has to replace the
        // builder that uses it.
        static Mesh cube;
        public static Mesh Cube()
        {
            if (cube != null) return cube;
            var v = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<Vector2>(); var tris = new List<int>();
            foreach (var f in new[] { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back })
            {
                var a = Mathf.Abs(f.y) > 0.5f ? Vector3.right : Vector3.up;
                var b = Vector3.Cross(a, f);   // a x b = -f: clockwise seen from outside, as Unity wants
                int i = v.Count;
                v.Add((f - a - b) * 0.5f); v.Add((f - a + b) * 0.5f); v.Add((f + a + b) * 0.5f); v.Add((f + a - b) * 0.5f);
                for (int k = 0; k < 4; k++) n.Add(f);
                t.Add(new Vector2(0, 0)); t.Add(new Vector2(0, 1)); t.Add(new Vector2(1, 1)); t.Add(new Vector2(1, 0));
                tris.Add(i); tris.Add(i + 1); tris.Add(i + 2); tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
            }
            cube = new Mesh { name = "MscTardisCube" };
            cube.vertices = v.ToArray(); cube.normals = n.ToArray(); cube.uv = t.ToArray(); cube.triangles = tris.ToArray();
            cube.RecalculateBounds();
            return cube;
        }

        public static GameObject Box(string name, Transform parent, Vector3 center, Vector3 size, Material mat, bool collider = false)
        {
            var go = MeshObject(name, Cube(), mat, parent);
            go.transform.localPosition = center;
            go.transform.localScale = size;
            if (collider) go.AddComponent<BoxCollider>();
            return go;
        }

        public static Material Mat(Color c)
        {
            return new Material(FindShader("Legacy Shaders/Diffuse")) { color = c };
        }

        // 3D text for signs and the console monitor. Reads correctly when looked at along the object's +Z. The font's
        // own material ignores depth and would show through walls, so it gets its own.
        public static TextMesh Text(string name, Transform parent, Vector3 pos, string text, float size, Color c, TextAnchor anchor = TextAnchor.MiddleCenter)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var tm = go.AddComponent<TextMesh>();
            tm.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            tm.fontSize = 64;
            tm.characterSize = size;
            tm.anchor = anchor;
            tm.color = c;
            tm.text = text;
#pragma warning disable 618   // compiling a shader from source is the only way to add one without a Unity editor
            var mat = new Material(TextShader) { mainTexture = tm.font.material.mainTexture, color = c };
#pragma warning restore 618
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return tm;
        }

        // Unlit, tinted by _Color, shaped by the font's alpha, and hidden by whatever is in front of it. The built-in
        // lit shaders draw an alpha-only font black.
        const string TextShader = @"Shader ""MscTardis/Text3D"" {
            Properties { _MainTex (""Font"", 2D) = ""white"" {} _Color (""Color"", Color) = (1,1,1,1) }
            SubShader {
                Tags { ""Queue"" = ""Transparent"" ""IgnoreProjector"" = ""True"" ""RenderType"" = ""Transparent"" }
                Lighting Off Cull Off ZWrite Off Fog { Mode Off }
                Blend SrcAlpha OneMinusSrcAlpha
                Pass {
                    SetTexture [_MainTex] { constantColor [_Color] combine constant, texture * constant }
                }
            }
        }";

        // Materials for the baked room, whose light lives in the vertex colours: texture x colour x 2 x _Color, as written. In the game the
        // DOUBLE does nothing (measured 2026-11-18: a 128 bake shows a wall at 0.45 of its texture), so a colour of 255 is the texture itself.
        // Lamp: unlit, texture x _Color x 2, for a lit lamp cap whatever the room's light.
        // Overlay: additive like Additive, but drawn last and with no depth test, so it glows through the console (the protocol halos).
        // Bright and BrightSelfIllum: Baked and SelfIllum with a stage that adds the light to itself, so 128 is the texture's own brightness and
        // 255 twice it. The 2010 room's bake is made for these (its walls are lit brighter than their textures).
        public enum RoomMode { Baked, Cutout, Glass, Additive, SelfIllum, Lamp, Overlay, Bright, BrightSelfIllum }
        static readonly Dictionary<RoomMode, Shader> roomShaders = new Dictionary<RoomMode, Shader>();

        public static Material RoomMat(string texFile, RoomMode mode, Color tint)
        {
            Shader s;
            Material mat;
#pragma warning disable 618
            if (!roomShaders.TryGetValue(mode, out s) || s == null)
            {
                mat = new Material(RoomShader(mode));
                roomShaders[mode] = mat.shader;
            }
            else mat = new Material(s);
#pragma warning restore 618
            mat.mainTexture = Tex(texFile, false, TextureWrapMode.Repeat);
            mat.color = tint;
            return mat;
        }

        static string RoomShader(RoomMode mode)
        {
            // Source's $selfillum: the texture's alpha marks what glows at full brightness, the rest takes the baked light.
            // The doubling stage of the Bright modes.
            string twice = mode == RoomMode.Bright || mode == RoomMode.BrightSelfIllum ? "SetTexture [_MainTex] { combine previous + previous, previous } " : "";
            if (mode == RoomMode.SelfIllum || mode == RoomMode.BrightSelfIllum)
                return "Shader \"MscTardis/Room" + mode + "\" { Properties { _MainTex (\"Tex\", 2D) = \"white\" {} _Color (\"Color\", Color) = (1,1,1,1) } "
                    + "SubShader { Tags { \"Queue\" = \"Geometry\" \"RenderType\" = \"Opaque\" } Lighting Off Cull Off Fog { Mode Off } Pass { "
                    + "BindChannels { Bind \"Vertex\", vertex Bind \"texcoord\", texcoord Bind \"Color\", color } "
                    + "SetTexture [_MainTex] { combine texture * primary DOUBLE } "
                    + twice
                    + "SetTexture [_MainTex] { constantColor [_Color] combine previous * constant } "
                    + "SetTexture [_MainTex] { combine texture lerp(texture) previous } "
                    + "} } }";
            if (mode == RoomMode.Lamp)
                return "Shader \"MscTardis/RoomLamp\" { Properties { _MainTex (\"Tex\", 2D) = \"white\" {} _Color (\"Color\", Color) = (1,1,1,1) } "
                    + "SubShader { Tags { \"Queue\" = \"Geometry\" \"RenderType\" = \"Opaque\" } Lighting Off Cull Off Fog { Mode Off } Pass { "
                    + "SetTexture [_MainTex] { constantColor [_Color] combine texture * constant DOUBLE } "
                    + "} } }";
            string tags = "\"Queue\" = \"Geometry\" \"RenderType\" = \"Opaque\"", state = "", alpha = "";
            if (mode == RoomMode.Cutout)
            {
                tags = "\"Queue\" = \"AlphaTest\" \"RenderType\" = \"TransparentCutout\"";
                state = "AlphaTest Greater 0.5";
                alpha = ", texture";
            }
            else if (mode == RoomMode.Glass)
            {
                tags = "\"Queue\" = \"Transparent\" \"IgnoreProjector\" = \"True\" \"RenderType\" = \"Transparent\"";
                state = "ZWrite Off Blend SrcAlpha OneMinusSrcAlpha";
                alpha = ", primary";
            }
            else if (mode == RoomMode.Additive)
            {
                tags = "\"Queue\" = \"Transparent\" \"IgnoreProjector\" = \"True\" \"RenderType\" = \"Transparent\"";
                state = "ZWrite Off Blend One One";
            }
            else if (mode == RoomMode.Overlay)
            {
                tags = "\"Queue\" = \"Transparent+100\" \"IgnoreProjector\" = \"True\" \"RenderType\" = \"Transparent\"";
                state = "ZWrite Off ZTest Always Blend One One";
            }
            return "Shader \"MscTardis/Room" + mode + "\" { Properties { _MainTex (\"Tex\", 2D) = \"white\" {} _Color (\"Color\", Color) = (1,1,1,1) } "
                + "SubShader { Tags { " + tags + " } Lighting Off Cull Off Fog { Mode Off } " + state + " Pass { "
                + "BindChannels { Bind \"Vertex\", vertex Bind \"texcoord\", texcoord Bind \"Color\", color } "
                + "SetTexture [_MainTex] { combine texture * primary DOUBLE" + alpha + " } "
                + twice
                + "SetTexture [_MainTex] { constantColor [_Color] combine previous * constant, previous } "
                + "} } }";
        }

        public static Mesh Mesh(string file)
        {
            Mesh m;
            if (!meshes.TryGetValue(file, out m)) meshes[file] = m = LoadObj(Path.Combine(dir, file));
            return m;
        }

        // The room's own binary mesh, written by tools/assets/build_interior.py: "DWM1", vertex and index counts,
        // then positions, UVs, colours (RGBA bytes) and indices. Already in Unity space.
        public static Mesh Dwm(string file)
        {
            Mesh m;
            if (meshes.TryGetValue(file, out m)) return m;
            using (var r = new BinaryReader(File.OpenRead(Path.Combine(dir, file))))
            {
                var magic = new string(r.ReadChars(4));
                if (magic != "DWM1" && magic != "DWM2") throw new Exception("not a dwm: " + file);
                int nv = r.ReadInt32(), ni = r.ReadInt32();
                var v = new Vector3[nv]; var t = new Vector2[nv]; var c = new Color32[nv]; var tris = new int[ni];
                for (int i = 0; i < nv; i++) v[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                for (int i = 0; i < nv; i++) t[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                for (int i = 0; i < nv; i++) c[i] = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
                Vector3[] n = null;
                if (magic == "DWM2")
                {
                    n = new Vector3[nv];
                    for (int i = 0; i < nv; i++) n[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                }
                for (int i = 0; i < ni; i++) tris[i] = r.ReadInt32();
                m = new Mesh { name = Path.GetFileNameWithoutExtension(file) };
                m.vertices = v; m.uv = t; m.colors32 = c; m.triangles = tris;
                if (n != null) m.normals = n;
                m.RecalculateBounds();
            }
            return meshes[file] = m;
        }

        // A copy of a mesh with only the triangles whose middles lie inside a box (in the mesh's own space), or only those outside it.
        // The mesh itself when that leaves it whole.
        public static Mesh Cut(Mesh src, Bounds box, bool keepInside)
        {
            var v = src.vertices;
            var t = src.triangles;
            var keep = new List<int>(t.Length);
            for (int i = 0; i + 2 < t.Length; i += 3)
                if (box.Contains((v[t[i]] + v[t[i + 1]] + v[t[i + 2]]) / 3f) == keepInside) { keep.Add(t[i]); keep.Add(t[i + 1]); keep.Add(t[i + 2]); }
            if (keep.Count == t.Length) return src;
            var m = (Mesh)UnityEngine.Object.Instantiate(src);
            m.name = src.name + (keepInside ? "_in" : "_out");
            m.triangles = keep.ToArray();
            return m;
        }

        public static string[] Lines(string file) { return File.ReadAllLines(Path.Combine(dir, file)); }

        // The photographic textures ship as JPEG (a quarter of the PNG's size), under the name the room files give with .png.
        // A .jpg wins over a .png of the same name, so a stale .png left in the Mods folder by an older version can't be picked.
        static string TexPath(string file)
        {
            var path = Path.Combine(dir, file);
            if (file.EndsWith(".png"))
            {
                var jpg = Path.ChangeExtension(path, ".jpg");
                if (File.Exists(jpg)) return jpg;
            }
            return path;
        }

        public static Texture2D Tex(string file, bool linear = false, TextureWrapMode wrap = TextureWrapMode.Clamp)
        {
            Texture2D t;
            if (textures.TryGetValue(file, out t)) return t;
            t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
            t.LoadImage(File.ReadAllBytes(TexPath(file)));
            t.wrapMode = wrap;
            t.anisoLevel = 4;
            t.name = file;
            return textures[file] = t;
        }

        public static AudioClip Clip(string file)
        {
            AudioClip c;
            if (clips.TryGetValue(file, out c)) return c;
            return clips[file] = LoadWav(Path.Combine(dir, file));
        }

        public static Material FindMaterial(string name)
        {
            return Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m => m.name == name);
        }

        public static Shader FindShader(string name)
        {
            var s = Shader.Find(name);
            return s != null ? s : Resources.FindObjectsOfTypeAll<Shader>().FirstOrDefault(x => x.name == name);
        }

        public static GameObject MeshObject(string name, Mesh mesh, Material mat, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        // OBJ already in Unity space: x right, y up, z forward, left-handed winding. Convert in the asset generator.
        static Mesh LoadObj(string path)
        {
            var v = new List<Vector3>();
            var vt = new List<Vector2>();
            var vn = new List<Vector3>();
            var outV = new List<Vector3>();
            var outT = new List<Vector2>();
            var outN = new List<Vector3>();
            var tris = new List<int>();
            var cache = new Dictionary<string, int>();
            var ci = CultureInfo.InvariantCulture;
            foreach (var raw in File.ReadAllLines(path))
            {
                if (raw.Length < 2 || raw[0] == '#') continue;
                var p = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                switch (p[0])
                {
                    case "v": v.Add(new Vector3(float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci))); break;
                    case "vt": vt.Add(new Vector2(float.Parse(p[1], ci), float.Parse(p[2], ci))); break;
                    case "vn": vn.Add(new Vector3(float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci))); break;
                    case "f":
                        var idx = new int[p.Length - 1];
                        for (int i = 1; i < p.Length; i++)
                        {
                            int id;
                            if (!cache.TryGetValue(p[i], out id))
                            {
                                var s = p[i].Split('/');
                                id = outV.Count;
                                outV.Add(v[int.Parse(s[0]) - 1]);
                                outT.Add(s.Length > 1 && s[1].Length > 0 ? vt[int.Parse(s[1]) - 1] : Vector2.zero);
                                outN.Add(s.Length > 2 && s[2].Length > 0 ? vn[int.Parse(s[2]) - 1] : Vector3.up);
                                cache[p[i]] = id;
                            }
                            idx[i - 1] = id;
                        }
                        for (int i = 1; i + 1 < idx.Length; i++) { tris.Add(idx[0]); tris.Add(idx[i]); tris.Add(idx[i + 1]); }
                        break;
                }
            }
            var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(path) };
            if (outV.Count > 65000) Debug.LogWarning("MscTardis: " + path + " has " + outV.Count + " vertices");
            mesh.vertices = outV.ToArray();
            mesh.uv = outT.ToArray();
            mesh.normals = outN.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }

        // 16-bit PCM WAV, mono or stereo.
        static AudioClip LoadWav(string path)
        {
            var b = File.ReadAllBytes(path);
            int ch = 1, rate = 44100, bits = 16, pos = 12, dataAt = -1, dataLen = 0;
            while (pos + 8 <= b.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int len = BitConverter.ToInt32(b, pos + 4);
                if (id == "fmt ") { ch = BitConverter.ToInt16(b, pos + 10); rate = BitConverter.ToInt32(b, pos + 12); bits = BitConverter.ToInt16(b, pos + 22); }
                else if (id == "data") { dataAt = pos + 8; dataLen = Math.Min(len, b.Length - dataAt); break; }
                pos += 8 + len + (len & 1);
            }
            if (dataAt < 0 || bits != 16) throw new Exception("unsupported wav " + path);
            int n = dataLen / 2;
            var d = new float[n];
            for (int i = 0; i < n; i++) d[i] = BitConverter.ToInt16(b, dataAt + i * 2) / 32768f;
            var clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), n / ch, ch, rate, false);
            clip.SetData(d, 0);
            return clip;
        }
    }
}
