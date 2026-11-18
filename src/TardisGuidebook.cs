using UnityEngine;

namespace MscTardis
{
    // The TARDIS guidebook: a loose item, picked up, carried, dropped and thrown like the game's own notepad (ITEMS/notepad(itemx)), whose
    // physics it copies: the hand's PickUp FSM takes anything tagged PART on the Parts layer, and the notepad's rigidbody settings (measured
    // off the live notepad) are read off the live notepad when there is one. The model is the user's (reference/model/tardis_guidebook,
    // converted by tools/assets/convert_guidebook.py): navy leather boards embossed with the police box doors, round the cream pages.
    // It starts on the console and keeps its place in the save. Lost (out of the world, or left behind in a room that was rebuilt), it goes back.
    static class Guidebook
    {
        // The PickUp FSM shows a picked thing's name less its last seven characters: the vanilla items all end in "(itemx)".
        const string ItemName = "tardis guidebook(itemx)";
        // From convert_guidebook.py's bounds: across the cover (spine at -x), through the boards, along the spine.
        static readonly Vector3 Size = new Vector3(0.1076f, 0.0209f, 0.1626f), Centre = new Vector3(0.0005f, -0.0002f, 0f);
        // The notepad's own settings, in case it can't be found (they're what `phys ITEMS/notepad(itemx)` printed).
        const float Mass = 0.5f, Drag = 0.08f, AngularDrag = 0.08f;
        const int PartsLayer = 19;

        static GameObject book;
        static Rigidbody rb;
        static float nextCheck;

        public static Transform T { get { return book != null ? book.transform : null; } }

        public static void Build()
        {
            var mesh = new Mesh { name = "tardis_guidebook" };
            mesh.CombineMeshes(new[]
            {
                new CombineInstance { mesh = Res.Mesh("tardis_guidebook_leather.obj"), transform = Matrix4x4.identity },
                new CombineInstance { mesh = Res.Mesh("tardis_guidebook_pages.obj"), transform = Matrix4x4.identity },
            }, false, true);
            book = new GameObject(ItemName);
            book.AddComponent<MeshFilter>().sharedMesh = mesh;
            book.AddComponent<MeshRenderer>().sharedMaterials = new[] { Leather(), Paper() };
            var box = book.AddComponent<BoxCollider>();
            box.size = Size;
            box.center = Centre;
            rb = book.AddComponent<Rigidbody>();
            var note = G.Find("ITEMS/notepad(itemx)");
            var like = note != null ? note.GetComponent<Rigidbody>() : null;
            if (like != null)
            {
                rb.mass = like.mass; rb.drag = like.drag; rb.angularDrag = like.angularDrag; rb.useGravity = like.useGravity;
                rb.interpolation = like.interpolation; rb.collisionDetectionMode = like.collisionDetectionMode; rb.maxAngularVelocity = like.maxAngularVelocity;
                book.layer = note.gameObject.layer;
                book.tag = note.tag;
            }
            else
            {
                rb.mass = Mass; rb.drag = Drag; rb.angularDrag = AngularDrag;
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
                book.layer = PartsLayer;
                book.tag = "PART";
            }

            var d = MscTardis.Data;
            if (d.BookPlaced)
            {
                book.transform.position = new Vector3(d.BookX, d.BookY, d.BookZ);
                book.transform.rotation = new Quaternion(d.BookQX, d.BookQY, d.BookQZ, d.BookQW);
            }
            else Home();
        }

        // The leather catches a little light, as the reference photo's does; the pages are plain paper.
        static Material Leather()
        {
            var spec = Res.FindShader("Legacy Shaders/Specular");
            var m = new Material(spec != null ? spec : Res.FindShader("Legacy Shaders/Diffuse")) { mainTexture = Res.Tex("tardis_guidebook_leather.png", false, TextureWrapMode.Clamp) };
            if (spec != null) { m.SetColor("_SpecColor", new Color(0.16f, 0.17f, 0.2f)); m.SetFloat("_Shininess", 0.12f); }
            return m;
        }

        static Material Paper()
        {
            return new Material(Res.FindShader("Legacy Shaders/Diffuse")) { mainTexture = Res.Tex("tardis_guidebook_pages.png", false, TextureWrapMode.Clamp) };
        }

        // Back to its place in the TARDIS, still, a few millimetres above whatever is there, to settle.
        public static string Home()
        {
            Vector3 at;
            Quaternion rot;
            TardisInterior.BookHome(out at, out rot);
            return Put(at, rot);
        }

        // Onto room-local (x, z), dropped from `top` (to try a spot).
        public static string At(float x, float z, float top)
        {
            Vector3 at;
            Quaternion rot;
            TardisInterior.BookSpot(new Vector3(x, top, z), out at, out rot);
            return Put(at, rot);
        }

        static string Put(Vector3 at, Quaternion rot)
        {
            if (book == null) return "no guidebook";
            if (G.InHand(book.transform)) G.DropFromHand();
            book.transform.SetParent(null);
            book.transform.position = at;
            book.transform.rotation = rot;
            rb.isKinematic = false;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            book.layer = PartsLayer;
            Save();
            return "guidebook put at room-local " + TardisInterior.Root.InverseTransformPoint(at).ToString("F3");
        }


        // The room it was left in has been rebuilt as another: it goes to the new room's console, unless the player is holding it.
        public static void RoomChanged()
        {
            if (book != null && !G.InHand(book.transform) && TardisInterior.InSpace(book.transform.position)) Home();
        }

        public static void Tick()
        {
            if (book != null && G.InHand(book.transform) && G.UsePressed) Read();
            if (book == null || Time.time < nextCheck) return;
            nextCheck = Time.time + 1f;
            if (G.InHand(book.transform)) return;
            var p = book.transform.position;
            // Through the map, or out of the room and falling to the map from up there.
            if (p.y < -100f || TardisInterior.Fallen(p)) Home();
        }

        // Reading it: Use while holding it shows the protocol IDs a few at a time, and the next Use turns the page.
        const int PerPage = 4;
        static int page;

        public static string Read()
        {
            int n = Protocols.Table.Length;
            int pages = (n + PerPage - 1) / PerPage;
            page = page % pages;
            var sb = new System.Text.StringBuilder("GUIDEBOOK, PROTOCOLS " + (page + 1) + "/" + pages + ":  ");
            for (int i = page * PerPage; i < Mathf.Min(n, (page + 1) * PerPage); i++)
            {
                if (i > page * PerPage) sb.Append("  |  ");
                sb.Append(Protocols.Table[i].Id + " " + Protocols.Table[i].Name);
            }
            page++;
            string text = sb.ToString();
            G.Flash(text, 7f);
            return text;
        }

        public static void Save()
        {
            if (book == null) return;
            var d = MscTardis.Data;
            var t = book.transform;
            d.BookPlaced = true;
            d.BookX = t.position.x; d.BookY = t.position.y; d.BookZ = t.position.z;
            d.BookQX = t.rotation.x; d.BookQY = t.rotation.y; d.BookQZ = t.rotation.z; d.BookQW = t.rotation.w;
        }

    }
}
