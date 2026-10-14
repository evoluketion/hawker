using Tmds.DBus.Protocol;

namespace Hawker.Web.Tray.Linux;

/// <summary>
/// The tray menu as a <c>com.canonical.dbusmenu</c> object, which StatusNotifierItem hosts draw themselves. Item ids
/// are the menu index plus one; id 0 is the root that holds them.
/// </summary>
public sealed class DbusMenu(IReadOnlyList<TrayMenuItem> items, Action<Action> dispatch) : IPathMethodHandler
{
    public const string ObjectPath = "/MenuBar";
    private const string Interface = "com.canonical.dbusmenu";
    private const string LayoutSignature = "(ia{sv}av)";

    private static readonly Dictionary<string, object> MenuProperties = new()
    {
        ["Version"] = 3u,
        ["TextDirection"] = "ltr",
        ["Status"] = "normal",
        ["IconThemePath"] = Array.Empty<string>(),
    };

    private IReadOnlyList<TrayMenuItem> _items = items;
    private uint _revision = 1;

    public string Path => ObjectPath;

    public bool HandlesChildPaths => false;

    public uint Revision => _revision;

    public IReadOnlyList<int> ChildIds => Enumerable.Range(1, _items.Count).ToList();

    /// <returns>The item's dbusmenu properties, or null for an unknown id.</returns>
    public IReadOnlyDictionary<string, object>? Properties(int id)
    {
        if (id == 0)
        {
            return new Dictionary<string, object> { ["children-display"] = "submenu" };
        }

        if (id < 1 || id > _items.Count)
        {
            return null;
        }

        var item = _items[id - 1];
        return item.IsSeparator
            ? new Dictionary<string, object> { ["type"] = "separator" }
            : new Dictionary<string, object> { ["label"] = item.Text, ["enabled"] = item.Enabled && item.OnClick is not null };
    }

    /// <returns>False for an unknown id.</returns>
    public bool Click(int id)
    {
        if (id < 1 || id > _items.Count)
        {
            return false;
        }

        if (_items[id - 1] is { Enabled: true, OnClick: { } onClick })
        {
            dispatch(onClick);
        }

        return true;
    }

    /// <summary>Swaps in new menu items and tells the host to fetch the layout again.</summary>
    public void Replace(IReadOnlyList<TrayMenuItem> newItems, DBusConnection? connection)
    {
        _items = newItems;
        var revision = ++_revision;
        if (connection is null)
        {
            return;
        }

        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteSignalHeader(null, ObjectPath, Interface, "LayoutUpdated", "ui");
            writer.WriteUInt32(revision);
            writer.WriteInt32(0);
            connection.TrySendMessage(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsPropertiesInterfaceRequest)
        {
            HandleProperties(context);
        }
        else if (context.Request.InterfaceAsString == Interface)
        {
            HandleMenu(context);
        }
        else
        {
            context.ReplyUnknownMethodError();
        }

        return ValueTask.CompletedTask;
    }

    private void HandleMenu(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        switch (context.Request.MemberAsString)
        {
            case "GetLayout":
            {
                var parent = reader.ReadInt32();
                if (Properties(parent) is null)
                {
                    context.ReplyError("com.canonical.dbusmenu.Error", $"No menu item {parent}.");
                    return;
                }

                Reply(context, "u" + LayoutSignature, (ref MessageWriter w) =>
                {
                    w.WriteUInt32(_revision);
                    WriteLayout(ref w, parent, withChildren: parent == 0);
                });
                return;
            }
            case "GetGroupProperties":
            {
                var requested = reader.ReadArrayOfInt32();
                int[] ids = requested.Length == 0 ? [0, .. ChildIds] : requested;
                Reply(context, "a(ia{sv})", (ref MessageWriter w) =>
                {
                    var array = w.WriteArrayStart(DBusType.Struct);
                    foreach (var id in ids)
                    {
                        if (Properties(id) is { } properties)
                        {
                            w.WriteStructureStart();
                            w.WriteInt32(id);
                            WriteProperties(ref w, properties);
                        }
                    }

                    w.WriteArrayEnd(array);
                });
                return;
            }
            case "GetProperty":
            {
                var id = reader.ReadInt32();
                var name = reader.ReadString();
                if (Properties(id) is { } properties && properties.TryGetValue(name, out var value))
                {
                    Reply(context, "v", (ref MessageWriter w) => WriteVariant(ref w, value));
                }
                else
                {
                    context.ReplyError("com.canonical.dbusmenu.Error", $"No property {name} on menu item {id}.");
                }

                return;
            }
            case "Event":
            {
                var id = reader.ReadInt32();
                if (reader.ReadString() == "clicked")
                {
                    Click(id);
                }

                Reply(context, "", (ref MessageWriter _) => { });
                return;
            }
            case "EventGroup":
            {
                var events = reader.ReadArrayStart(DBusType.Struct);
                while (reader.HasNext(events))
                {
                    reader.AlignStruct();
                    var id = reader.ReadInt32();
                    var eventId = reader.ReadString();
                    reader.ReadVariantValue();
                    reader.ReadUInt32();
                    if (eventId == "clicked")
                    {
                        Click(id);
                    }
                }

                // The reply lists ids that had no item; unknown ids are ignored instead.
                Reply(context, "ai", (ref MessageWriter w) => w.WriteArray(Array.Empty<int>()));
                return;
            }
            case "AboutToShow":
                Reply(context, "b", (ref MessageWriter w) => w.WriteBool(false));
                return;
            case "AboutToShowGroup":
                Reply(context, "aiai", (ref MessageWriter w) =>
                {
                    w.WriteArray(Array.Empty<int>());
                    w.WriteArray(Array.Empty<int>());
                });
                return;
            default:
                context.ReplyUnknownMethodError();
                return;
        }
    }

    private static void HandleProperties(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var member = context.Request.MemberAsString;
        if (reader.ReadString() != Interface || member is not ("Get" or "GetAll"))
        {
            context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", "Menu properties are read-only.");
            return;
        }

        if (member == "GetAll")
        {
            Reply(context, "a{sv}", (ref MessageWriter w) => WriteProperties(ref w, MenuProperties));
        }
        else if (MenuProperties.TryGetValue(reader.ReadString(), out var value))
        {
            Reply(context, "v", (ref MessageWriter w) => WriteVariant(ref w, value));
        }
        else
        {
            context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", "No such property.");
        }
    }

    private void WriteLayout(ref MessageWriter writer, int id, bool withChildren)
    {
        writer.WriteStructureStart();
        writer.WriteInt32(id);
        WriteProperties(ref writer, Properties(id)!);
        var children = writer.WriteArrayStart(DBusType.Variant);
        if (withChildren)
        {
            foreach (var child in ChildIds)
            {
                // A variant holding a nested layout: its signature, then the struct itself.
                writer.WriteSignature(LayoutSignature);
                WriteLayout(ref writer, child, withChildren: false);
            }
        }

        writer.WriteArrayEnd(children);
    }

    private static void WriteProperties(ref MessageWriter writer, IReadOnlyDictionary<string, object> properties)
    {
        var dict = writer.WriteDictionaryStart();
        foreach (var (key, value) in properties)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(key);
            WriteVariant(ref writer, value);
        }

        writer.WriteDictionaryEnd(dict);
    }

    private static void WriteVariant(ref MessageWriter writer, object value)
    {
        switch (value)
        {
            case string s:
                writer.WriteVariantString(s);
                break;
            case bool b:
                writer.WriteVariantBool(b);
                break;
            case uint u:
                writer.WriteVariantUInt32(u);
                break;
            case string[] strings:
                writer.WriteSignature("as");
                writer.WriteArray(strings);
                break;
            default:
                throw new ArgumentException($"Unsupported menu property type {value.GetType()}.", nameof(value));
        }
    }

    internal delegate void BodyWriter(ref MessageWriter writer);

    /// <summary>MessageWriter is a ref struct, so it is passed by ref and disposed here rather than with using.</summary>
    internal static void Reply(MethodContext context, string signature, BodyWriter write)
    {
        var writer = context.CreateReplyWriter(signature);
        try
        {
            write(ref writer);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }
}
