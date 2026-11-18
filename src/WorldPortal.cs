using System;
using UnityEngine;

namespace MscTardis
{
    // One side of the doorway: a frame and the surface that shows, live, what the other side would see from where the viewer stands.
    // A port of the rendering in GMod's worldportals library (FUNCTION.md; section numbers below are its).
    //
    // The frame is unscaled. Its origin is the middle of the doorway at floor level, and its forward points out of the doorway, at the
    // viewer. The surface is not a flat sheet in the doorway but a box set back behind it with its front open (section 4, "inverted
    // thick"), so the view can come right up to the doorway, and stand in it, without the near plane cutting a hole in the picture.
    //
    // FUNCTION.md's shader samples the picture at each pixel's own place on screen. That needs a compiled shader, which needs a
    // Unity 5.0 editor, which this project doesn't have. So the mesh carries the screen position instead: it is rebuilt every frame,
    // each vertex's UV where the camera puts it on screen (UpdateMesh). The GPU interpolates UVs in perspective, which is linear across the triangle in the
    // world, not on the screen, so a triangle whose corners are at different depths is out in its middle: by about its length on
    // screen times its depth change over its depth, over 4. So each face is cut into strips along lines of equal depth, and every
    // corner of a strip is slid along its own line of sight to the strip's nearest depth (Flatten). It stays where it was on the
    // screen, and with the whole strip at one depth the interpolation is linear on the screen: exact, whatever the triangles' shape.
    // The strip is drawn up to one strip's depth nearer than the surface really is (StripRatio), and reaches a little way into the
    // next one so the seams between them can't open (Overlap). Without the slide the long, thin triangles were a few pixels out on
    // faces seen edge-on, in a sawtooth.
    class WorldPortal
    {
        public const float Width = 1.034f, Height = 2.2f;   // the police box's door opening, which both sides share
        public const float Recess = 0.4f;                   // how far back the surface's box goes
        const float Inset = 0.003f, FloorLift = 0.004f;     // clear of the door posts, the lintel and the floor it sits on
        public static float StripRatio = 1.005f;            // a strip's far depth over its near one
        public static float Overlap = 0.3f;                 // how far, in strips, each strip reaches into the next
        public static bool Flatten = true;                  // each strip at one depth (off: as cut, with the sawtooth; for comparing)
        const int MaxStrips = 240, MaxVerts = 9000;
        static readonly Quaternion Flip = Quaternion.Euler(0f, 180f, 0f);

        public readonly Transform Frame;
        public readonly Material Mat;
        public WorldPortal Exit;
        public Action PreRender, PostRender;   // around the other side's picture being drawn: show or hide things for it only (section 7)

        readonly GameObject surface;
        readonly Renderer rend;
        readonly Mesh mesh;
        readonly Vector3[] verts = new Vector3[MaxVerts];
        readonly Vector2[] uvs = new Vector2[MaxVerts];
        readonly int[] tris = new int[MaxVerts * 3];
        readonly Vector3[][] faces;
        readonly Vector3[] corners = new Vector3[8];
        readonly Bounds bounds;
        int nv, nt, lastNt;
        bool dim;

        // Scratch for cutting a face up: the piece being cut, the strip cut off it, and what is left.
        static readonly Vector3[] polyA = new Vector3[16], polyB = new Vector3[16], polyC = new Vector3[16];
        static Matrix4x4 toClip;   // frame space to the viewer's clip space, for the face being cut

        public WorldPortal(string name, Transform parent, Vector3 localPos, Quaternion localRot, Shader shader, Material depth, int layer)
        {
            Frame = new GameObject(name).transform;
            Frame.SetParent(parent, false);
            Frame.localPosition = localPos;
            Frame.localRotation = localRot;

            float x0 = -Width / 2f + Inset, x1 = Width / 2f - Inset, y0 = FloorLift, y1 = Height - Inset, z0 = -Recess, z1 = 0f;
            faces = new[]
            {
                new[] { new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y0, z0) },   // back
                new[] { new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0), new Vector3(x0, y0, z0) },   // sides
                new[] { new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1) },
                new[] { new Vector3(x0, y0, z1), new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1) },   // floor
                new[] { new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0) },   // top
            };
            int k = 0;
            foreach (float x in new[] { x0, x1 })
                foreach (float y in new[] { y0, y1 })
                    foreach (float z in new[] { z0, z1 })
                        corners[k++] = new Vector3(x, y, z);
            bounds = new Bounds(new Vector3(0f, Height / 2f, -Recess / 2f), new Vector3(Width, Height, Recess));

            surface = new GameObject("Surface");
            surface.transform.SetParent(Frame, false);
            if (layer >= 0) surface.layer = layer;
            Mat = new Material(shader) { color = Dark };
            var mr = surface.AddComponent<MeshRenderer>();
            mesh = new Mesh { name = name };
            mesh.MarkDynamic();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.bounds = bounds;
            mr.sharedMaterial = Mat;
            surface.AddComponent<MeshFilter>().sharedMesh = mesh;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            rend = mr;

            // The same box again, fixed, in `depth` (a game shader the deferred G-buffer takes), under the picture: it puts the doorway's
            // depth where the game camera's fog reads it. Without it the fog sees what is behind the doorway (the far side of the hollow
            // box, or the sky) and hazes the picture as if it were the horizon.
            if (depth != null)
            {
                var dgo = new GameObject("SurfaceDepth");
                dgo.transform.SetParent(surface.transform, false);
                dgo.layer = surface.layer;
                depthMesh = BoxMesh(name + "Depth");
                dgo.AddComponent<MeshFilter>().sharedMesh = depthMesh;
                var dr = dgo.AddComponent<MeshRenderer>();
                dr.sharedMaterial = depth;
                dr.receiveShadows = false;
                dr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;   // the G-buffer holds its depth (`portal shadows` to check)
                depthRend = dr;
            }
            dim = true;
            surface.SetActive(false);
        }

        readonly Mesh depthMesh;
        readonly Renderer depthRend;

        Mesh BoxMesh(string name)
        {
            var v = new Vector3[faces.Length * 4];
            var t = new int[faces.Length * 6];
            for (int f = 0; f < faces.Length; f++)
            {
                for (int i = 0; i < 4; i++) v[f * 4 + i] = faces[f][i];
                int[] q = { 0, 2, 1, 0, 3, 2 };   // facing in, at the viewer: `faces` wind the other way (the picture's shader draws both sides)
                for (int i = 0; i < 6; i++) t[f * 6 + i] = f * 4 + q[i];
            }
            var m = new Mesh { name = name, vertices = v, triangles = t };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }


        static readonly Color Dark = new Color(0.02f, 0.02f, 0.03f, 1f);

        // The surface is only there while its door is open.
        public bool Shown
        {
            get { return surface != null && surface.activeSelf; }
            set { if (surface != null && surface.activeSelf != value) surface.SetActive(value); }
        }

        // Out of every picture while one is being drawn: portals don't show in portals (section 9).
        public bool Drawn { set { if (rend != null) rend.enabled = value; if (depthRend != null) depthRend.enabled = value; } }

        // With no picture to show (too far off, or the portals switched off), the doorway is dark.
        public bool Dim
        {
            get { return dim; }
            set { if (dim == value) return; dim = value; Mat.color = value ? Dark : Color.white; }
        }

        public void SetPicture(Texture t) { Mat.mainTexture = t; }

        // This frame's view. clip takes frame space to the viewer's clip space (projection * view * frame); the picture is looked up at
        // (0.5 + 0.5 x / w, 0.5 + 0.5 y / w), each pixel's own place on the screen. eye is the viewer in frame space; near, the depth
        // the mesh is cut at (just past the camera's near plane, so a strip slid to its nearest depth isn't clipped away).
        public void SetView(Matrix4x4 clip, float near, bool flipY, Vector3 eye) { UpdateMesh(clip, near, flipY, eye); }

        public void Destroy()
        {
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            if (depthMesh != null) UnityEngine.Object.Destroy(depthMesh);
            if (Mat != null) UnityEngine.Object.Destroy(Mat);
            if (Frame != null) UnityEngine.Object.Destroy(Frame.gameObject);
        }

        // Section 3.2: a pose on this side to the same pose seen from the other side.
        public void TransformPose(Vector3 p, Quaternion q, out Vector3 outP, out Quaternion outQ)
        {
            var inv = Quaternion.Inverse(Frame.rotation);
            var local = Flip * (inv * (p - Frame.position));
            outP = Exit.Frame.position + Exit.Frame.rotation * local;
            outQ = Exit.Frame.rotation * Flip * inv * q;
        }

        // Any of the surface's box inside the view: its corners aren't all off the same side of the screen.
        public bool InView(Matrix4x4 clip, float near)
        {
            int left = 0, right = 0, below = 0, above = 0, behind = 0;
            for (int i = 0; i < 8; i++)
            {
                var c = clip * new Vector4(corners[i].x, corners[i].y, corners[i].z, 1f);
                if (c.x < -c.w) left++;
                if (c.x > c.w) right++;
                if (c.y < -c.w) below++;
                if (c.y > c.w) above++;
                if (c.w < near) behind++;
            }
            return left < 8 && right < 8 && below < 8 && above < 8 && behind < 8;
        }

        // Rebuild the surface for this frame's view.
        void UpdateMesh(Matrix4x4 clip, float near, bool flipY, Vector3 eye)
        {
            toClip = clip;
            viewEye = eye;
            nv = nt = 0;
            float ratio = Mathf.Max(StripRatio, 1.0005f);
            foreach (var face in faces)
            {
                for (int i = 0; i < 4; i++) polyC[i] = face[i];
                int nLow, n;
                Split(polyC, 4, near, polyB, out nLow, polyA, out n);   // what is nearer than the near plane is never drawn
                if (n < 3) continue;
                float lo = float.MaxValue, hi = 0f;
                for (int i = 0; i < n; i++) { float w = Depth(polyA[i]); if (w < lo) lo = w; if (w > hi) hi = w; }
                lo = Mathf.Max(lo, near);
                int strips = Mathf.Clamp(Mathf.CeilToInt(Mathf.Log(hi / lo) / Mathf.Log(ratio)), 1, MaxStrips);
                float step = Mathf.Pow(hi / lo, 1f / strips), level = lo;
                float reach = Flatten ? Mathf.Pow(step, Mathf.Clamp01(Overlap)) : 1f;   // flat strips at different depths overlap; cut ones share their seams
                for (int s = 0; s < strips - 1 && n >= 3; s++)
                {
                    level *= step;
                    int nb, nc;
                    if (reach > 1f)
                    {
                        Split(polyA, n, level * reach, polyB, out nb, polyC, out nc);   // this strip, a little into the next
                        Emit(polyB, nb, flipY);
                        Split(polyA, n, level, polyB, out nb, polyC, out nc);           // and what is left for the rest
                    }
                    else
                    {
                        Split(polyA, n, level, polyB, out nb, polyC, out nc);
                        Emit(polyB, nb, flipY);
                    }
                    for (int i = 0; i < nc; i++) polyA[i] = polyC[i];
                    n = nc;
                }
                Emit(polyA, n, flipY);
            }
            for (int i = nt * 3; i < lastNt * 3; i++) tris[i] = 0;   // last frame's leftovers: no area
            lastNt = nt;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.bounds = bounds;   // setting the triangles works the bounds out again, from vertices that aren't all in use
        }

        public int Triangles { get { return nt; } }
        public Renderer Renderer { get { return rend; } }
        public Mesh Mesh { get { return mesh; } }

        static float Depth(Vector3 p) { return toClip.m30 * p.x + toClip.m31 * p.y + toClip.m32 * p.z + toClip.m33; }

        // A convex polygon cut along the line of equal depth `level`: the nearer part and the farther part.
        static void Split(Vector3[] src, int n, float level, Vector3[] near, out int nNear, Vector3[] far, out int nFar)
        {
            nNear = nFar = 0;
            var prev = src[n - 1];
            float dp = Depth(prev) - level;
            for (int i = 0; i < n; i++)
            {
                var cur = src[i];
                float dc = Depth(cur) - level;
                if ((dp < 0f) != (dc < 0f))
                {
                    var x = prev + (cur - prev) * (dp / (dp - dc));
                    near[nNear++] = x;
                    far[nFar++] = x;
                }
                if (dc < 0f) near[nNear++] = cur; else far[nFar++] = cur;
                prev = cur;
                dp = dc;
            }
        }

        static Vector3 viewEye;   // the viewer in frame space, for the face being cut

        void Emit(Vector3[] poly, int n, bool flipY)
        {
            if (n < 3 || nv + n > MaxVerts) return;
            float flat = float.MaxValue;   // the depth the whole piece is slid to: its nearest
            if (Flatten) for (int i = 0; i < n; i++) flat = Mathf.Min(flat, Depth(poly[i]));
            for (int i = 0; i < n; i++)
            {
                var p = poly[i];
                float w = Depth(p);
                float x = toClip.m00 * p.x + toClip.m01 * p.y + toClip.m02 * p.z + toClip.m03;
                float y = toClip.m10 * p.x + toClip.m11 * p.y + toClip.m12 * p.z + toClip.m13;
                float v = 0.5f + 0.5f * y / w;
                // Depth is linear and 0 at the eye, so along the line from the eye the point at depth `flat` is a fraction flat / w of
                // the way. Same place on the screen, so the same UV.
                verts[nv + i] = Flatten ? viewEye + (p - viewEye) * (flat / w) : p;
                uvs[nv + i] = new Vector2(0.5f + 0.5f * x / w, flipY ? 1f - v : v);
            }
            for (int i = 1; i + 1 < n; i++)
            {
                tris[nt * 3] = nv;
                tris[nt * 3 + 1] = nv + i;
                tris[nt * 3 + 2] = nv + i + 1;
                nt++;
            }
            nv += n;
        }
    }

    // The game switches each area's props off by their distance from the player's camera: a "LOD" FSM on each (the yard 300 m, the store
    // 400 m, the village church 200 m, the parked vehicles 300 m, ...). In the room the camera is 500 m up, so the world seen through
    // the open door would be missing them. Those FSMs' distance checks are pointed at a stand-in instead: on the camera outside, at the
    // police box's doorway inside, as if the player were standing in the box. Their global SavePlayerCam is left alone.
    // The map's ambience, heard from the room through the open doorway. While the player is in the room the sound objects stand where
    // the doorway puts the map, as seen from the room, so the birds, the dog and the lake are as loud, and on the same side, either side
    // of the step. (With the doors shut the room mutes them: TardisInterior.MuteAmbience.) They are plain sound objects: no colliders,
    // and their FSMs set only volumes. Each step jumps the listener, and these with it, hundreds of metres in a frame, so their Doppler
    // is off for that frame and a couple after.
    static class PortalAudio
    {
        static readonly string[] Roots = { "MAP/SoundAmbience", "MAP/LakeSound", "MAP/LakeSound 2" };
        static readonly System.Collections.Generic.List<Transform> roots = new System.Collections.Generic.List<Transform>();
        static readonly System.Collections.Generic.List<Vector3> homeP = new System.Collections.Generic.List<Vector3>();
        static readonly System.Collections.Generic.List<Quaternion> homeQ = new System.Collections.Generic.List<Quaternion>();
        static readonly System.Collections.Generic.List<AudioSource> hushed = new System.Collections.Generic.List<AudioSource>();
        static readonly System.Collections.Generic.List<float> hushedLevel = new System.Collections.Generic.List<float>();
        static bool mapped;
        static int hushUntil;
        const bool Enabled = true;

        public static void Place(bool inside)
        {
            if (inside && Enabled && Doorway.Outer != null)
            {
                if (!mapped)
                {
                    roots.Clear(); homeP.Clear(); homeQ.Clear();
                    foreach (var path in Roots)
                    {
                        var go = GameObject.Find(path);
                        if (go == null || go.GetComponentsInChildren<Collider>(true).Length > 0) continue;
                        roots.Add(go.transform); homeP.Add(go.transform.position); homeQ.Add(go.transform.rotation);
                    }
                    mapped = true;
                }
                for (int i = 0; i < roots.Count; i++)
                {
                    if (roots[i] == null) continue;
                    Vector3 p; Quaternion q;
                    Doorway.Outer.TransformPose(homeP[i], homeQ[i], out p, out q);
                    roots[i].position = p;
                    roots[i].rotation = q;
                }
            }
            else if (mapped) Home();
            PortalEcho.Tick(inside && Enabled && Doorway.Outer != null && Tardis.DoorOpen);
            if (hushUntil != 0 && Time.frameCount >= hushUntil)
            {
                for (int i = 0; i < hushed.Count; i++) if (hushed[i] != null) hushed[i].dopplerLevel = hushedLevel[i];
                hushed.Clear(); hushedLevel.Clear();
                hushUntil = 0;
            }
        }

        public static void Home()
        {
            for (int i = 0; i < roots.Count; i++)
                if (roots[i] != null) { roots[i].position = homeP[i]; roots[i].rotation = homeQ[i]; }
            roots.Clear(); homeP.Clear(); homeQ.Clear();
            mapped = false;
            PortalEcho.Clear();
        }

        // One of the map's ambience roots moved through the doorway (PortalEcho leaves these alone).
        public static bool IsAmbience(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.name == "SoundAmbience" || t.name == "LakeSound" || t.name == "LakeSound 2")
                    if (t.parent != null && t.parent.name == "MAP" && t.parent.parent == null) return true;
            return false;
        }

        // Just after the step through, in the same frame (before the sound is mixed).
        public static void Crossed()
        {
            if (hushUntil == 0)
                foreach (var path in Roots)
                {
                    var go = GameObject.Find(path);
                    if (go == null) continue;
                    foreach (var s in go.GetComponentsInChildren<AudioSource>(true)) { hushed.Add(s); hushedLevel.Add(s.dopplerLevel); s.dopplerLevel = 0f; }
                }
            hushUntil = Time.frameCount + 3;
        }

    }

    // The sounds of things that can't be moved through the doorway as PortalAudio moves the ambience: a car left running, the boat, the
    // tractor, a radio. They sit on bodies with colliders, so while the player is in the room with the doors open, each playing 3D sound
    // in earshot of the doorway gets an echo: a copy of its source standing where the doorway puts it, playing the same clip from the same
    // sample, its volume and pitch copied every frame. The original is muted meanwhile (it is 500 m off anyway). Stepping in, the echo
    // starts in the same frame, so nothing drops. Finding the game's sources takes about 3 ms, so it is done every 30 s near the box, not
    // on the step: the step checks that pool whole, and after it 150 entries a frame catch sounds that start later. Custom rolloff curves can't be read in Unity 5.0 (no GetCustomCurve), so those sources
    // (MasterAudio's pooled one-shots, mostly) are left alone; so are one-shots played with PlayOneShot, which have no clip to follow.
    static class PortalEcho
    {
        const float Range = 200f;          // cap on how far from the doorway a sound is taken, whatever its own range
        const float PoolEvery = 30f;       // seconds between full searches for the game's sources (a search takes about 3 ms)
        const int PerFrame = 150;          // pool entries looked at a frame for sounds that started since
        sealed class Echo { public AudioSource src, copy; public bool muted; public int bornAt; }
        static readonly System.Collections.Generic.List<Echo> echoes = new System.Collections.Generic.List<Echo>();
        static readonly System.Collections.Generic.HashSet<AudioSource> taken = new System.Collections.Generic.HashSet<AudioSource>();
        static Transform holder;
        static bool active;
        static float poolAt = -1000f;
        static readonly System.Collections.Generic.List<AudioSource> pool = new System.Collections.Generic.List<AudioSource>();
        static int next;
        public static int Count { get { return echoes.Count; } }

        public static void Tick(bool on)
        {
            bool stale = Time.time >= poolAt + PoolEvery;
            if (!on)
            {
                if (active) Clear();
                // Kept fresh near the box, so the step in finds it ready and doesn't pay for the search.
                if (stale && Doorway.Outer != null && G.Player != null && (MscTardis.Data.Inside || (G.Player.position - Tardis.Position).sqrMagnitude < 2500f)) Pool();
                return;
            }
            if (pool.Count == 0 || (active && stale)) Pool();
            if (!active) Check(0, pool.Count);   // the step in: everything at once, in this frame
            else { if (next >= pool.Count) next = 0; int n = Mathf.Min(PerFrame, pool.Count - next); Check(next, n); next += n; }
            active = true;
            for (int i = echoes.Count - 1; i >= 0; i--)
            {
                var e = echoes[i];
                if (e.src == null || e.copy == null || !e.src.isActiveAndEnabled || !e.src.isPlaying || e.src.clip == null)
                { Drop(i); continue; }
                Follow(e);
            }
        }

        // Every 3D source that could be echoed, whatever it is doing now: not the player's, the room's, the box's or the moved ambience.
        static void Pool()
        {
            poolAt = Time.time;
            pool.Clear(); next = 0;
            var player = G.Player != null ? G.Player.root : null;
            var room = TardisInterior.Root != null ? TardisInterior.Root.root : null;
            var box = Tardis.Root != null ? Tardis.Root.root : null;
            var cam = G.Cam != null ? G.Cam.transform.root : null;
            foreach (var s in UnityEngine.Object.FindObjectsOfType<AudioSource>())
            {
                if (s.spatialBlend < 0.01f || s.rolloffMode == AudioRolloffMode.Custom) continue;
                var t = s.transform;
                var r = t.root;
                if (r == player || r == room || r == box || r == cam || (holder != null && r == holder)) continue;
                if (PortalAudio.IsAmbience(t)) continue;
                pool.Add(s);
            }
        }

        static void Check(int from, int n)
        {
            var door = Doorway.Outer.Frame.position;
            for (int i = from; i < from + n; i++)
            {
                var s = pool[i];
                if (s == null || taken.Contains(s) || !s.isPlaying || s.clip == null) continue;
                if ((s.transform.position - door).sqrMagnitude > Sq(Mathf.Min(s.maxDistance, Range))) continue;
                Add(s);
            }
        }

        static float Sq(float f) { return f * f; }

        static void Add(AudioSource s)
        {
            if (holder == null) holder = new GameObject("MscTardisPortalEcho").transform;
            var go = new GameObject("echo " + s.name);
            go.transform.parent = holder;
            var c = go.AddComponent<AudioSource>();
            c.playOnAwake = false;
            c.clip = s.clip;
            c.outputAudioMixerGroup = s.outputAudioMixerGroup;
            c.loop = s.loop;
            c.priority = s.priority;
            c.spatialBlend = s.spatialBlend;
            c.rolloffMode = s.rolloffMode;
            c.minDistance = s.minDistance;
            c.maxDistance = s.maxDistance;
            c.spread = s.spread;
            c.dopplerLevel = 0f;   // its first frames jump from nowhere; Follow gives it the source's after that
            c.panStereo = s.panStereo;
            c.reverbZoneMix = s.reverbZoneMix;
            c.bypassEffects = s.bypassEffects;
            c.bypassListenerEffects = s.bypassListenerEffects;
            c.bypassReverbZones = s.bypassReverbZones;
            var e = new Echo { src = s, copy = c, bornAt = Time.frameCount };
            Place(e);
            c.volume = s.volume;
            c.pitch = s.pitch;
            c.mute = s.mute;
            c.timeSamples = s.timeSamples;
            c.Play();
            if (!s.mute) { s.mute = true; e.muted = true; }
            echoes.Add(e);
            taken.Add(s);
        }

        static void Place(Echo e)
        {
            Vector3 p; Quaternion q;
            Doorway.Outer.TransformPose(e.src.transform.position, e.src.transform.rotation, out p, out q);
            e.copy.transform.position = p;
            e.copy.transform.rotation = q;
        }

        static void Follow(Echo e)
        {
            var s = e.src; var c = e.copy;
            Place(e);
            if (c.clip != s.clip) { c.clip = s.clip; c.timeSamples = s.timeSamples; c.Play(); }
            c.loop = s.loop;
            c.volume = s.volume;
            c.pitch = s.pitch;
            c.minDistance = s.minDistance;
            c.maxDistance = s.maxDistance;
            c.spatialBlend = s.spatialBlend;
            if (Time.frameCount >= e.bornAt + 3) c.dopplerLevel = s.dopplerLevel;
            // A source the game had muted itself stays the game's to unmute, and the echo follows it; one muted here stays muted.
            if (!e.muted) { if (!s.mute) { s.mute = true; e.muted = true; c.mute = false; } else c.mute = true; }
            if (!c.isPlaying) { c.timeSamples = s.timeSamples; c.Play(); }
            else if (s.clip != null && Mathf.Abs(c.timeSamples - s.timeSamples) > s.clip.frequency / 20) c.timeSamples = s.timeSamples;
        }

        static void Drop(int i)
        {
            var e = echoes[i];
            if (e.src != null && e.muted) e.src.mute = false;
            if (e.copy != null) UnityEngine.Object.Destroy(e.copy.gameObject);
            if (e.src != null) taken.Remove(e.src);
            echoes.RemoveAt(i);
        }

        public static void Clear()
        {
            for (int i = echoes.Count - 1; i >= 0; i--) Drop(i);
            echoes.Clear();
            taken.Clear();
            active = false;
        }

        // A new box (load, save reload): the pool is searched afresh.
        public static void Reset()
        {
            Clear();
            pool.Clear(); next = 0;
            poolAt = -1000f;
        }

    }

    static class LodEye
    {
        static Transform eye;
        static readonly System.Collections.Generic.HashSet<object> patched = new System.Collections.Generic.HashSet<object>();

        public static void Build()
        {
            patched.Clear();
            done.Clear();
            next = 0;
            eye = new GameObject("MscTardisLodEye").transform;
            Place();
            Scan();
        }

        // Any LOD FSM not seen yet (vehicles that were inactive at load initialise later). Uninitialised FSMs are skipped, not forced.
        // The whole search takes about 75 ms, so it is done at load only; after that the FSMs running are looked through a few
        // hundred a frame (Tick), and one switched on since is found within a second or so.
        public static int Scan()
        {
            if (eye == null) return 0;
            int added = 0;
            foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                if (f != null && f.gameObject.hideFlags == HideFlags.None) added += Patch(f);
            return added;
        }

        static readonly System.Collections.Generic.HashSet<PlayMakerFSM> done = new System.Collections.Generic.HashSet<PlayMakerFSM>();
        static int next;
        const int PerFrame = 300;

        static void ScanSome()
        {
            var list = PlayMakerFSM.FsmList;
            if (list == null || list.Count == 0) return;
            for (int i = 0; i < PerFrame && i < list.Count; i++)
            {
                if (next >= list.Count) next = 0;
                var f = list[next++];
                if (f != null && !done.Contains(f)) Patch(f);
            }
        }

        static int Patch(PlayMakerFSM f)
        {
            if (done.Contains(f)) return 0;
            if (!IsLod(f)) { done.Add(f); return 0; }
            int added = 0;
            bool seen = false;
            try
            {
                foreach (var st in f.FsmStates)
                {
                    var acts = st.Actions;
                    if (acts == null) continue;
                    seen = true;
                    foreach (var act in acts)
                    {
                        if (act == null || act.GetType().Name != "GetDistance" || patched.Contains(act)) continue;
                        var tf = act.GetType().GetField("target");
                        var tv = tf != null ? tf.GetValue(act) as HutongGames.PlayMaker.FsmGameObject : null;
                        if (tv == null || !(tv.Name == "SavePlayerCam" || tv.Value == G.Cam.gameObject)) continue;
                        tf.SetValue(act, new HutongGames.PlayMaker.FsmGameObject { Value = eye.gameObject });
                        patched.Add(act);
                        added++;
                    }
                }
            }
            catch (Exception) { }
            if (seen) done.Add(f);   // not yet initialised: looked at again on a later pass
            return added;
        }

        // "LOD", "LOD_Consumption", "ReijoLOD", and the "Activate" FSMs that keep a LOD distance. Not the gameplay checks that also measure
        // the distance to the player (crimes, deaths, NPC talk, opening hours).
        static bool IsLod(PlayMakerFSM f)
        {
            var n = f.FsmName;
            if (n == null) return false;
            if (n.IndexOf("LOD", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            try { return n == "Activate" && f.FsmVariables.FindFsmFloat("LOD") != null; } catch (Exception) { return false; }
        }

        public static void Tick()
        {
            if (eye == null) return;
            Place();
            ScanSome();
        }

        static void Place()
        {
            bool inRoom = MscTardis.Data.Inside && !Pilot.Active && Doorway.Outer != null;
            eye.position = inRoom ? Doorway.Outer.Frame.position + Vector3.up * 1.6f : G.Cam.transform.position;
        }

    }

    // On the game's own camera: draws the doorway's picture just before that camera draws the world.
    class PortalDriver : MonoBehaviour
    {
        Camera cam;

        // After every Update, the player's walk included (the mod's own tick can run before the game moves the player that frame).
        void LateUpdate() { if (Doorway.CrossLate) Doorway.CheckCross(); }

        void OnPreCull()
        {
            if (cam == null) cam = GetComponent<Camera>();
            Doorway.Render(cam);
        }
    }

    // The doorway between the police box and the room: the two portals, the camera that draws their pictures, and the player walking
    // through. Use on a door opens it (Tardis.ToggleDoor); this is what an open door then does.
    static class Doorway
    {
        // Walking room in the doorway is wider than what shows of it, so the player's body (wider than the view) fits between the posts.
        public const float WalkHalf = 0.58f, WalkDepth = 0.62f;
        const float MaxDistance = 200f;   // beyond this the doorway is a few pixels: not worth drawing the other side for (at 60 m it was still big enough for its going dark to show)
        const float ClipOffset = 0.0127f, ObliqueMargin = 0.037f;
        // The picture from outside keeps this much behind the room's plane: shut or nearly, the room's copy of the police box's leaves
        // is up to 8 cm behind it (their fronts), and a clip any nearer cuts their faces away as they close.
        public static float OuterClip = 0.1f;
        public static bool NearBox = true;   // looking out, an ordinary near plane where that does (NearInBox)
        public static bool RainThrough = true;   // with the doors open, the rain goes on outside, seen through them (TardisInterior.PlaceRain)
        const bool Oblique = true;

        public static WorldPortal Outer, Inner;   // in the police box's door opening; in the room's
        public static bool Enabled = true, FlipY, ToggleSurfaces;
        public static RenderingPath Path = RenderingPath.Forward;   // the portal camera's: an oblique near plane needs forward in Unity 5.0 (to check)
        public static float Scale = 1f;           // of the picture against the screen: 0.5 draws a quarter of the pixels

        static Camera cam;
        static RenderTexture rt;
        static Shader shader;
        static Material depthMat;
        static int layer = -1;
        static bool failed;
        static string skipped = "nothing drawn yet";
        static System.Reflection.FieldInfo motorMovement, motorVelocity;
        static Component motor;

        // Unlit: the picture as it is, or dark while there is none. Pulled a hair nearer, so it wins over the depth pass under it.
        const string PortalShader = @"Shader ""MscTardis/Portal"" {
            Properties { _MainTex (""Tex"", 2D) = ""black"" {} _Color (""Color"", Color) = (1,1,1,1) }
            SubShader {
                Tags { ""Queue"" = ""Geometry+1"" ""RenderType"" = ""Opaque"" }
                Lighting Off Cull Off Fog { Mode Off } Offset -1, -1
                Pass { SetTexture [_MainTex] { constantColor [_Color] combine texture * constant } }
            }
        }";

        // Once per load, with the police box. The room's side comes with the room (SetInner).
        public static void Build(Transform box, Vector3 localPos)
        {
            Release();
            PortalEcho.Reset();
            Outer = Inner = null;
            failed = false;
            skipped = "nothing drawn yet";
            motor = null;
#pragma warning disable 618   // compiling a shader from source is the only way to add one without a Unity editor
            shader = new Material(PortalShader).shader;
#pragma warning restore 618
            if (depthMat != null) UnityEngine.Object.Destroy(depthMat);
            var diffuse = Res.FindShader("Legacy Shaders/Diffuse");
            depthMat = diffuse != null ? new Material(diffuse) { color = Color.black } : null;
            layer = FreeLayer();

            cam = new GameObject("MscTardisPortalCam").AddComponent<Camera>();
            cam.enabled = false;
            if (G.Cam.GetComponent<PortalDriver>() == null) G.Cam.gameObject.AddComponent<PortalDriver>();

            Outer = new WorldPortal("PortalOuter", box, localPos, Quaternion.identity, shader, depthMat, layer);
            Outer.PreRender = () => TardisInterior.ShowForPortal(true);    // the room is hidden from outside; it shows for this picture only
            Outer.PostRender = () => TardisInterior.ShowForPortal(false);
            LodEye.Build();
        }

        public static void SetInner(Transform room, Vector3 localPos, Quaternion localRot)
        {
            if (Inner != null) Inner.Destroy();
            Inner = new WorldPortal("PortalInner", room, localPos, localRot, shader, depthMat, layer);
            Inner.Exit = Outer;
            Outer.Exit = Inner;
            if (rt != null) Inner.SetPicture(rt);
        }

        // A layer nothing in the game is on and the game's camera draws, so the portal camera can leave the surfaces out.
        static int FreeLayer()
        {
            for (int i = 31; i >= 8; i--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i)) && (G.Cam.cullingMask & (1 << i)) != 0) return i;
            return -1;   // none: the surfaces are switched off around each picture instead, which is done anyway
        }

        static void Release()
        {
            if (rt == null) return;
            rt.Release();
            UnityEngine.Object.Destroy(rt);
            rt = null;
        }

        // ---------------------------------------------------------------------------------------------------------
        // The picture (section 4), drawn from the game camera's OnPreCull, when that camera is where it will draw from.

        public static void Render(Camera viewer)
        {
            if (failed || Outer == null || Inner == null || viewer == null) return;
            try { RenderSide(viewer); }
            catch (Exception e)
            {
                failed = true;   // once, not every frame
                MSCLoader.ModConsole.Error("MSC-TARDIS: the doorway's picture failed and is off: " + e.Message);
                Debug.LogException(e);
            }
        }

        // The doorway the game's camera looks at: the room's while the player is in the room, the police box's anywhere else. Piloting the
        // box, the player is still counted inside but the camera is out in the world, looking at the box from outside, and the doors can be
        // open then (flight with them open, FLIGHT's lever thrown): the room is what shows in the box's doorway, as from anyone outside.
        static WorldPortal Seen { get { return MscTardis.Data.Inside && !Pilot.Active ? Inner : Outer; } }

        static void RenderSide(Camera viewer)
        {
            var p = Seen;
            if (!p.Shown) { skipped = "door shut"; return; }
            if (!Enabled) { p.Dim = true; skipped = "switched off"; return; }

            var at = p.Frame.InverseTransformPoint(viewer.transform.position);
            if (at.z <= 0f) { skipped = "viewer behind the doorway"; return; }
            var clip = viewer.projectionMatrix * viewer.worldToCameraMatrix * p.Frame.localToWorldMatrix;
            float near = viewer.nearClipPlane * 0.9f;
            if (!p.InView(clip, near)) { skipped = "out of view"; return; }

            p.SetView(clip, WorldPortal.Flatten ? viewer.nearClipPlane * 1.001f : near, FlipY, at);
            if (at.sqrMagnitude > MaxDistance * MaxDistance) { p.Dim = true; skipped = "too far"; return; }

            EnsureTexture(viewer);
            // Switching the surfaces off round the picture keeps them out of it when there is no free layer to do that. It costs:
            // a renderer turned off and on again inside OnPreCull isn't culled back in for the camera that is about to draw.
            bool toggle = layer < 0 || ToggleSurfaces;
            if (toggle) Outer.Drawn = Inner.Drawn = false;
            if (p.PreRender != null) p.PreRender();
            try { RenderView(viewer, p); }
            finally
            {
                if (p.PostRender != null) p.PostRender();
                if (toggle) Outer.Drawn = Inner.Drawn = true;
            }
            p.Dim = false;
            skipped = null;
        }

        static void EnsureTexture(Camera viewer)
        {
            int w = Mathf.Max(16, Mathf.RoundToInt(viewer.pixelWidth * Scale)), h = Mathf.Max(16, Mathf.RoundToInt(viewer.pixelHeight * Scale));
            var format = viewer.hdr ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
            int aa = 1;   // the game's camera is deferred, with no MSAA (its FXAA goes over the picture too): one with MSAA was smoother at the edges than the world round it
            if (rt != null && rt.width == w && rt.height == h && rt.format == format && rt.antiAliasing == aa) return;
            Release();
            rt = new RenderTexture(w, h, 24, format);
            rt.antiAliasing = aa;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.Create();
            Outer.SetPicture(rt);
            Inner.SetPicture(rt);
        }

        static void RenderView(Camera src, WorldPortal p)
        {
            cam.CopyFrom(src);   // also the transform and the layer; the pose is set below
            cam.enabled = false;
            cam.targetTexture = rt;
            cam.depthTextureMode = DepthTextureMode.None;   // the game's camera wants one for its fog; this one has no effects to feed
            cam.useOcclusionCulling = false;
            if (Path != RenderingPath.UsePlayerSettings) cam.renderingPath = Path;   // UsePlayerSettings: the game camera's, as copied
            if (layer >= 0) cam.cullingMask = src.cullingMask & ~(1 << layer);

            Vector3 pos; Quaternion rot;
            p.TransformPose(src.transform.position, src.transform.rotation, out pos, out rot);
            cam.transform.position = pos;
            cam.transform.rotation = rot;

            // Section 3.4: nothing behind the other doorway's plane is drawn (the wall it is set in, the box it stands in).
            var exit = p.Exit.Frame;
            var n = exit.forward;
            var proj = src.projectionMatrix;
            float offset = p == Outer ? OuterClip : ClipOffset;   // only when the camera is well behind that plane: never one behind or at it
            float near;
            bool oblique = false;
            if (Oblique && Vector3.Dot(n, exit.position - pos) > offset + ObliqueMargin)
            {
                if (p == Inner && NearBox && NearInBox(exit, pos, rot * Vector3.forward, src.nearClipPlane, out near))
                {
                    cam.nearClipPlane = near;
                    proj = cam.projectionMatrix;   // the copied camera's own, with that near plane
                }
                else { MakeOblique(ref proj, CameraSpacePlane(cam, exit.position, n, offset)); oblique = true; }
            }
            // Looking out with an ordinary projection, the world is drawn as the game's camera draws it: the same path (deferred, so the
            // same lighting and shadows), and the fog the game's CameraFog switches on for that camera alone, which in deferred reaches
            // what is drawn forward (trees, water). The room looked into from outside is unlit and needs neither.
            bool same = p == Inner && !oblique && SamePath;
            if (same) cam.renderingPath = src.renderingPath;
            // And when the game's own fog effect is on out there (rain sets it going: GlobalFog, which fogs what deferred draws, from the depth)
            // the same effect on this camera. The room switches the game camera's off (TardisInterior.ApplyInside), so outside is where it shows.
            bool globalFog = FogLikeGame(same && TardisInterior.WorldGlobalFog);
            cam.depthTextureMode = globalFog ? DepthTextureMode.Depth : DepthTextureMode.None;
            cam.projectionMatrix = proj;
            if (same) GameFog(true);
            try { cam.Render(); }
            finally { if (same) GameFog(false); }
            cam.ResetProjectionMatrix();
            cam.targetTexture = null;
        }

        const bool SamePath = true;
        static bool GlobalFogCopy = true;   // the game's GlobalFog effect on the view out, when it is on outside
        static Component gameGlobalFog;
        static Behaviour portalGlobalFog;
        static float fogCopiedAt = -1f;

        // A copy of the game camera's GlobalFog on the portal camera, its settings copied over (each second: rain changes them), on or off.
        static bool FogLikeGame(bool on)
        {
            on &= GlobalFogCopy;
            if (on && gameGlobalFog == null) gameGlobalFog = G.Cam.GetComponent("GlobalFog");
            if (gameGlobalFog == null) on = false;
            if (on && (portalGlobalFog == null || Time.time >= fogCopiedAt + 1f))
            {
                try
                {
                    var t = gameGlobalFog.GetType();
                    if (portalGlobalFog == null) portalGlobalFog = cam.gameObject.AddComponent(t) as Behaviour;
                    for (var bt = t; bt != null && bt != typeof(MonoBehaviour); bt = bt.BaseType)
                        foreach (var f in bt.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                            f.SetValue(portalGlobalFog, f.GetValue(gameGlobalFog));
                    fogCopiedAt = Time.time;
                }
                catch (Exception e)
                {
                    GlobalFogCopy = false;
                    MSCLoader.ModConsole.Error("MSC-TARDIS: no fog in the doorway's picture: " + e.Message);
                    on = false;
                }
            }
            if (portalGlobalFog != null && portalGlobalFog.enabled != on) portalGlobalFog.enabled = on;
            return on;
        }

        // The game camera's CameraFog: switched on round that camera's render, and back off after (so not round this one's).
        // Its settings are public fields (Enabled, Mode, Density, Color, StartDistance, EndDistance). Looked up as properties, they weren't
        // found and nothing was applied: unseen in clear weather (density 0.001), but rain's 0.02 left the view out clear and the step out
        // into the haze.
        static Behaviour cameraFog;
        static System.Reflection.FieldInfo fogOn, fogMode, fogDensity, fogColour, fogStart, fogEnd;
        static bool savedFog;
        static FogMode savedMode;
        static float savedDensity, savedStart, savedEnd;
        static Color savedColour;
        static bool fogApplied;
        static void GameFog(bool on)
        {
            if (!on)
            {
                if (!fogApplied) return;
                RenderSettings.fog = savedFog; RenderSettings.fogMode = savedMode; RenderSettings.fogDensity = savedDensity;
                RenderSettings.fogColor = savedColour; RenderSettings.fogStartDistance = savedStart; RenderSettings.fogEndDistance = savedEnd;
                fogApplied = false;
                return;
            }
            if (cameraFog == null)
            {
                cameraFog = G.Cam.GetComponent("CameraFog") as Behaviour;
                if (cameraFog == null) return;
                var t = cameraFog.GetType();
                fogOn = t.GetField("Enabled"); fogMode = t.GetField("Mode"); fogDensity = t.GetField("Density");
                fogColour = t.GetField("Color"); fogStart = t.GetField("StartDistance"); fogEnd = t.GetField("EndDistance");
            }
            if (!cameraFog.enabled || fogOn == null || fogMode == null || fogDensity == null || fogColour == null || fogStart == null || fogEnd == null) return;
            savedFog = RenderSettings.fog; savedMode = RenderSettings.fogMode; savedDensity = RenderSettings.fogDensity;
            savedColour = RenderSettings.fogColor; savedStart = RenderSettings.fogStartDistance; savedEnd = RenderSettings.fogEndDistance;
            fogApplied = true;
            RenderSettings.fog = (bool)fogOn.GetValue(cameraFog);
            RenderSettings.fogMode = (FogMode)fogMode.GetValue(cameraFog);
            RenderSettings.fogDensity = (float)fogDensity.GetValue(cameraFog);
            RenderSettings.fogColor = (Color)fogColour.GetValue(cameraFog);
            RenderSettings.fogStartDistance = (float)fogStart.GetValue(cameraFog);
            RenderSettings.fogEndDistance = (float)fogEnd.GetValue(cameraFog);
        }

        // Looking out, the camera stands in the police box or behind it, and what is behind the box's doorway (the box, whatever stands
        // behind it) mustn't be drawn. The oblique near plane does that, but in Unity 5.0 it loses the sun's shadows (the picture's ground
        // was bright and the step through darkened it). An ordinary near plane, at the nearest the camera sees of the opening, cuts away
        // all that is nearer; what it leaves before the doorway's plane is a sliver of the view through the opening, and if that sliver is
        // inside the box, which is empty while the doors are open, there is nothing in it to cut. Its far corners are where the rays to the
        // opening's corners cross that near plane: all four inside the box, it is (the box is convex).
        static readonly float[] cornerDepth = new float[4];
        static readonly Vector3[] corner = new Vector3[4];
        // With the camera itself in the box (the step through, or just before it), all it sees of the doorway's far side is through
        // the box's empty inside, and its own near plane will do.
        static bool NearInBox(Transform exit, Vector3 pos, Vector3 forward, float minNear, out float near)
        {
            near = minNear;
            if (Tardis.InBox(pos)) return true;
            near = float.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                corner[i] = exit.TransformPoint(new Vector3((i & 1) == 0 ? -WorldPortal.Width / 2f : WorldPortal.Width / 2f, i < 2 ? 0f : WorldPortal.Height, 0f));
                cornerDepth[i] = Vector3.Dot(corner[i] - pos, forward);
                if (cornerDepth[i] <= minNear) return false;   // some of the opening is beside or behind the camera
                near = Mathf.Min(near, cornerDepth[i]);
            }
            near *= 0.995f;
            for (int i = 0; i < 4; i++)
                if (!Tardis.InBox(pos + (corner[i] - pos) * (near / cornerDepth[i]))) return false;
            return true;
        }

        // A plane in the camera's space (right-handed, looking down -Z), keeping the side its normal points to.
        static Vector4 CameraSpacePlane(Camera c, Vector3 pos, Vector3 normal, float offset)
        {
            var m = c.worldToCameraMatrix;
            var cpos = m.MultiplyPoint(pos - normal * offset);
            var cnormal = m.MultiplyVector(normal).normalized;
            return new Vector4(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal));
        }

        // Eric Lengyel, "Oblique View Frustum Depth Projection and Clipping": the near plane becomes the given plane.
        // (Camera.CalculateObliqueMatrix isn't in Unity 5.0.)
        static void MakeOblique(ref Matrix4x4 proj, Vector4 plane)
        {
            var q = proj.inverse * new Vector4(Sgn(plane.x), Sgn(plane.y), 1f, 1f);
            var c = plane * (2f / Vector4.Dot(plane, q));
            proj[2] = c.x - proj[3];
            proj[6] = c.y - proj[7];
            proj[10] = c.z - proj[11];
            proj[14] = c.w - proj[15];
        }

        static float Sgn(float a) { return a > 0f ? 1f : a < 0f ? -1f : 0f; }

        // ---------------------------------------------------------------------------------------------------------
        // Walking through. The view is what crosses: as it passes the doorway's plane the player is moved to the same spot on the
        // other side, facing and speed kept. The surface's box behind the plane means there is no frame where the plane is too
        // near to draw.

        // The view is in the doorway on its own side: in its walking room, or near enough in front of it that the plug (which reaches a
        // hand's width out past the leaves) would close on the player's body.
        public static bool PlayerInDoorway()
        {
            if (Outer == null || Inner == null) return false;
            var p = MscTardis.Data.Inside ? Inner : Outer;
            var c = p.Frame.InverseTransformPoint(G.Cam.transform.position);
            return Mathf.Abs(c.x) < WalkHalf + 0.1f && c.y > -0.5f && c.y < WorldPortal.Height + 0.3f && c.z > -WalkDepth && c.z < 0.55f;
        }

        // When to look for a step through: in the game camera's LateUpdate, after the game has moved the player for the frame (true), or
        // in the mod's tick, which can run before that and leave a frame drawn from just past the plane (false; `portal late` to compare).
        public static bool CrossLate = true;

        public static void Tick()
        {
            if (Outer == null || Inner == null) return;
            LodEye.Tick();
            if (!CrossLate) CheckCross();
        }

        public static void CheckCross()
        {
            if (Outer == null || Inner == null) return;
            if (!Tardis.Passable || Pilot.Active || G.Seated || Computer.Open) return;
            var p = MscTardis.Data.Inside ? Inner : Outer;
            if (PastPlane(p, G.Cam.transform.position)) Cross(p);
        }

        // The view is behind this side's doorway, in its walking room: time to be on the other side.
        static bool PastPlane(WorldPortal p, Vector3 view)
        {
            var c = p.Frame.InverseTransformPoint(view);
            return c.z < 0f && c.z >= -WalkDepth && Mathf.Abs(c.x) <= WalkHalf && c.y >= -0.3f && c.y <= WorldPortal.Height + 0.2f;
        }



        static void Cross(WorldPortal p)
        {
            Vector3 pos; Quaternion rot;
            p.TransformPose(G.Player.position, G.Player.rotation, out pos, out rot);
            float yaw = rot.eulerAngles.y, turn = Mathf.DeltaAngle(G.Player.eulerAngles.y, yaw);
            G.Player.position = pos;
            G.Player.rotation = Quaternion.Euler(0f, yaw, 0f);
            TurnVelocity(Quaternion.Euler(0f, turn, 0f));

            bool inside = p == Outer;
            MscTardis.Data.Inside = inside;
            TardisInterior.OnCrossed();
            if (!inside) Systems.OnPlayerLeft();
        }

        // The walk carries on in the new direction: the game's CharacterMotor keeps its velocity in world space.
        static void TurnVelocity(Quaternion turn)
        {
            try
            {
                if (motor == null)
                {
                    motor = G.Player.GetComponent("CharacterMotor");
                    if (motor == null) return;
                    motorMovement = motor.GetType().GetField("movement");
                    motorVelocity = motorMovement != null ? motorMovement.FieldType.GetField("velocity") : null;
                }
                if (motorVelocity == null) return;
                var movement = motorMovement.GetValue(motor);
                motorVelocity.SetValue(movement, turn * (Vector3)motorVelocity.GetValue(movement));
            }
            catch (Exception) { motorVelocity = null; }   // without it the walk slows for a step and picks up again
        }







    }
}
