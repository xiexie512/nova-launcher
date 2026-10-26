using System.Security.Cryptography;
using System.Text;
using NovaNet.Engine;

namespace NovaNet.Server;

/// <summary>
/// 房间管理：创建/加入/离开/踢人、虚拟 IP 分配、成员广播。
/// 虚拟 IP 形如 10.55.0.x（x=2..254），房主固定 .2，先到先得，离房回收。
/// </summary>
public sealed class RoomManager
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Room> _roomsByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Room> _roomsById = new();
    private readonly int _maxMembersPerRoom;

    public RoomManager(int maxMembersPerRoom = 20) => _maxMembersPerRoom = maxMembersPerRoom;

    public int RoomCount { get { lock (_lock) return _roomsByCode.Count; } }
    public int MemberCount { get { lock (_lock) return _roomsByCode.Values.Sum(r => r.Members.Count); } }

    // ================= 房间操作 =================

    public RoomSnapshot? CreateRoom(HubConnection conn, string name, string? password)
    {
        lock (_lock)
        {
            var room = new Room
            {
                Id = NewHex(8),
                Code = GenRoomCode(),
                Name = string.IsNullOrWhiteSpace(name) ? "未命名房间" : name.Trim(),
                PasswordHash = string.IsNullOrEmpty(password) ? null : Sha256(password),
                OwnerMemberId = "",
                MaxMembers = _maxMembersPerRoom,
            };
            var owner = new MemberRecord
            {
                MemberId = NewHex(16),
                ClientId = conn.ClientId,
                Name = string.IsNullOrWhiteSpace(conn.Name) ? "房主" : conn.Name.Trim(),
                VirtualIp = NnConst.VirtualPrefix + "2",
                PublicUdp = conn.PublicUdp,
                LocalUdp = conn.LocalUdp,
                IsOwner = true,
                Conn = conn,
            };
            conn.Member = owner;
            room.OwnerMemberId = owner.MemberId;
            room.Members.Add(owner);
            room.UsedIps.Add(2);
            _roomsByCode[room.Code] = room;
            _roomsById[room.Id] = room;
            Console.WriteLine($"[Hub] 房间创建 {room.Code} · {room.Name} · 房主 {owner.Name}");
            return BuildSnapshot(room, owner);
        }
    }

    /// <summary>把连接绑定到成员（创建/加入后调用，替换 Hello 阶段的临时连接）。</summary>
    public RoomSnapshot? JoinRoom(HubConnection conn, string code, string? password)
    {
        lock (_lock)
        {
            if (!_roomsByCode.TryGetValue(code.Trim().ToUpperInvariant(), out var room))
                throw new RoomException("房间不存在，请检查房间码");

            if (room.PasswordHash != null)
            {
                if (string.IsNullOrEmpty(password) || Sha256(password) != room.PasswordHash)
                    throw new RoomException("房间密码错误");
            }
            if (room.Members.Count >= room.MaxMembers)
                throw new RoomException($"房间已满（上限 {room.MaxMembers} 人）");

            var ip = NextFreeIp(room);
            var member = new MemberRecord
            {
                MemberId = NewHex(16),
                ClientId = conn.ClientId,
                Name = string.IsNullOrWhiteSpace(conn.Name) ? "玩家" : conn.Name.Trim(),
                VirtualIp = ip,
                PublicUdp = conn.PublicUdp,
                LocalUdp = conn.LocalUdp,
                IsOwner = false,
            };
            member.Conn = conn;
            conn.Member = member;
            room.Members.Add(member);
            room.UsedIps.Add(LastOctet(ip));
            Console.WriteLine($"[Hub] {member.Name} 加入房间 {room.Code} · 虚拟IP {member.VirtualIp} · 成员 {room.Members.Count}/{room.MaxMembers}");

            // 通知房间内其他成员：新成员加入
            foreach (var other in room.Members)
            {
                if (other == member) continue;
                _ = HubServer.SendAsync(other.Conn, new { t = "member", member = ToDto(member) });
                _ = HubServer.SendAsync(other.Conn, new { t = "punch_req", from = ToDto(member) });
            }
            return BuildSnapshot(room, member);
        }
    }

    /// <summary>成员离开（主动 leave 或连接断开）。</summary>
    public void Leave(HubConnection conn, string reason = "left")
    {
        lock (_lock)
        {
            var member = conn.Member;
            if (member == null || member.Conn == null) return;
            var room = FindRoomOf(member);
            if (room == null) return;
            RemoveMember(room, member, reason);
        }
    }

    /// <summary>房主踢人。</summary>
    public void Kick(HubConnection conn, string targetMemberId)
    {
        lock (_lock)
        {
            var owner = conn.Member;
            if (owner == null || !owner.IsOwner) throw new RoomException("只有房主可以移出成员");
            var room = FindRoomOf(owner);
            if (room == null) throw new RoomException("不在房间中");
            var target = room.Members.FirstOrDefault(m => m.MemberId == targetMemberId);
            if (target == null) throw new RoomException("成员不存在");
            RemoveMember(room, target, "kicked");
            _ = HubServer.SendAsync(target.Conn, new { t = "kicked", reason = "你已被房主移出房间" });
        }
    }

    /// <summary>成员断线：视为离开，房间为空则销毁。</summary>
    public void OnDisconnect(HubConnection conn)
    {
        lock (_lock)
        {
            var member = conn.Member;
            if (member == null) return;
            var room = FindRoomOf(member);
            if (room == null) return;
            RemoveMember(room, member, "left");
        }
    }

    private void RemoveMember(Room room, MemberRecord member, string reason)
    {
        room.Members.Remove(member);
        room.UsedIps.Remove(LastOctet(member.VirtualIp));
        member.Conn = null;
        Console.WriteLine($"[Hub] {member.Name} 离开房间 {room.Code}（{reason}）· 剩余 {room.Members.Count} 人");
        foreach (var other in room.Members)
            _ = HubServer.SendAsync(other.Conn, new { t = "gone", memberId = member.MemberId, reason });
        if (room.Members.Count == 0)
        {
            _roomsByCode.Remove(room.Code);
            _roomsById.Remove(room.Id);
            Console.WriteLine($"[Hub] 房间 {room.Code} 已销毁（无成员）");
        }
    }

    // ================= 转发 =================

    /// <summary>把 punch 请求转发给目标成员。</summary>
    public bool ForwardPunch(HubConnection from, string targetMemberId, string msgT)
    {
        lock (_lock)
        {
            var fromMember = from.Member;
            if (fromMember == null) return false;
            var room = FindRoomOf(fromMember);
            if (room == null) return false;
            var target = room.Members.FirstOrDefault(m => m.MemberId == targetMemberId);
            if (target == null || target.Conn == null) return false;
            _ = HubServer.SendAsync(target.Conn, new { t = msgT, from = ToDto(fromMember) });
            return true;
        }
    }

    /// <summary>中继二进制帧：校验同房间后原样转发。</summary>
    public bool Relay(HubConnection from, byte[] frameBytes, string toMemberId)
    {
        lock (_lock)
        {
            var fromMember = from.Member;
            if (fromMember == null) return false;
            var room = FindRoomOf(fromMember);
            if (room == null) return false;
            var target = room.Members.FirstOrDefault(m => m.MemberId == toMemberId);
            if (target == null || target.Conn == null) return false;
            target.Conn.SendBinary(frameBytes);
            return true;
        }
    }

    // ================= 快照与 DTO =================

    public sealed record RoomSnapshot(Room Room, MemberRecord Member, string MyMemberId)
    {
        public object ToPayload() => new
        {
            t = "room",
            myMemberId = MyMemberId,
            room = new
            {
                id = Room.Id,
                code = Room.Code,
                name = Room.Name,
                myVirtualIp = Member.VirtualIp,
                isOwner = Member.IsOwner,
                hasPassword = Room.PasswordHash != null,
            },
            members = Room.Members.Select(ToDto).ToArray(),
        };

        public static object ToDto(MemberRecord m) => new
        {
            m.MemberId,
            m.Name,
            m.VirtualIp,
            m.PublicUdp,
            m.LocalUdp,
            online = m.Conn != null,
            m.IsOwner,
            m.JoinedAt,
        };
    }

    private static RoomSnapshot BuildSnapshot(Room room, MemberRecord member)
        => new(room, member, member.MemberId);

    private static object ToDto(MemberRecord m) => RoomSnapshot.ToDto(m);

    /// <summary>按成员查找所在房间（Hub 转发用）。</summary>
    public Room? FindRoom(MemberRecord m) => FindRoomOf(m);

    private Room? FindRoomOf(MemberRecord m)
        => _roomsById.Values.FirstOrDefault(r => r.Members.Contains(m));

    // ================= 工具 =================

    private static string NewHex(int len)
    {
        Span<byte> b = stackalloc byte[len];
        RandomNumberGenerator.Fill(b);
        var sb = new StringBuilder(len * 2);
        foreach (var x in b) sb.Append(x.ToString("x2"));
        return sb.ToString(0, len);
    }

    private static string GenRoomCode()
    {
        Span<byte> b = stackalloc byte[6];
        RandomNumberGenerator.Fill(b);
        var sb = new StringBuilder(6);
        foreach (var x in b) sb.Append(NnConst.CodeAlphabet[x % NnConst.CodeAlphabet.Length]);
        return sb.ToString();
    }

    private static string Sha256(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static string NextFreeIp(Room room)
    {
        for (var i = 2; i <= 254; i++)
        {
            if (room.UsedIps.Contains(i)) continue;
            room.UsedIps.Add(i);
            return NnConst.VirtualPrefix + i;
        }
        throw new RoomException("房间 IP 已耗尽");
    }

    private static int LastOctet(string ip)
    {
        var i = ip.LastIndexOf('.');
        return int.TryParse(i >= 0 ? ip[(i + 1)..] : "0", out var o) ? o : 0;
    }
}

public sealed class RoomException : Exception
{
    public RoomException(string msg) : base(msg) { }
}
