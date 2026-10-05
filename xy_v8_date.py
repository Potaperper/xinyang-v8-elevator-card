#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
鑫洋V8 电梯卡 —— 日期码 / 校验码 计算器 + 卡密码计算器
=======================================================

两个功能，都很简单：

【1】改有效期：卡上只有 3 个字节跟这事有关。输入这 3 个字节的旧值 + 旧日期 + 新日期，
     吐出 3 个新字节。它不需要知道卡的密码，也不需要知道卡里那套"一卡一密"——
     因为脚本算的是"变化量"，每张卡自己的那套密码在做差时会自己抵消掉。

【2】算卡密码：鑫洋V8 系列的扇区密码由卡号(UID)直接算出来，
     所以"读到 UID 就能把卡读开"，不需要解卡、不需要嗅探。
     （这是用来**读卡**的密码；跟【1】里那串"逐字节加密密钥流"是两回事。）

用法
----
  (交互式，推荐给小白)   python xy_v8_date.py
  (改期·命令行)         python xy_v8_date.py 2024-03-15 2029-06-30 A3 5C B0
  (改期·直接吃 dump)     python xy_v8_date.py 2024-03-15 2029-06-30 卡.dump
  (改楼层)              python xy_v8_date.py --floor 2-11 1-11 "1C 93 5E B0 27 84 6A" B0
  (改其它字段·通用)      python xy_v8_date.py --field 4E12 1234 5E71 B0
  (算密码)              python xy_v8_date.py --key 11223344
  (算密码·从 dump 取UID) python xy_v8_date.py --key 卡.dump
  (自检)                python xy_v8_date.py --selftest

改期参数顺序： 当前有效期  目标有效期  日期高字节  日期低字节  校验码
（后三个是卡上"扇区1"里的十六进制值，见 README 的图示）
"""

import sys
import os
import datetime

# ---------------------------------------------------------------- 两个基础运算
def bitrev(b):
    """整字节逐位倒序（bit reverse）： 0xAB -> 0xD5"""
    r = 0
    for i in range(8):
        if (b >> i) & 1:
            r |= 1 << (7 - i)
    return r


def pack(y, m, d):
    """日期打包成一个 16 位数： (年-2000)*512 + 月*32 + 日"""
    return ((y - 2000) << 9) | (m << 5) | d


def parse(s):
    """把 '2024-03-15' / '2024.3.15' / '2024/3/15' 解析成 (年,月,日)"""
    s = s.strip().replace('/', '-').replace('.', '-')
    y, m, d = (int(x) for x in s.split('-'))
    return y, m, d


# ---------------------------------------------------------------- 核心：就这 8 行
def calc(old_date, new_date, hi, lo, chk):
    """
    hi  = 扇区1 块1 第6字节（日期高字节）
    lo  = 扇区1 块1 第7字节（日期低字节）
    chk = 扇区1 块0 第1字节（校验码）
    返回 (新的日期高字节, 新的日期低字节, 新的校验码)
    """
    a = pack(*parse(old_date))
    b = pack(*parse(new_date))
    dh = (a >> 8) ^ (b >> 8)          # 高字节的变化量
    dl = (a & 0xFF) ^ (b & 0xFF)      # 低字节的变化量
    return (hi ^ bitrev(dh),
            lo ^ bitrev(dl),
            chk ^ bitrev(dh) ^ bitrev(dl))   # 校验码 = 两个变化量异或后再异或上去


# ---------------------------------------------------------------- 交互式界面
def ask(msg, example=''):
    tip = f'（例：{example}）' if example else ''
    try:
        v = input(f'{msg}{tip}: ').strip()
    except EOFError:              # 管道/无人值守时用默认值，不崩
        print(f'{msg}{tip}: <采用默认 {example}>')
        return example
    if v:
        return v
    if example:
        return example
    print('  ↑ 不能为空，请重新输入')
    return example


def hexbyte(msg, example=''):
    while True:
        v = ask(msg, example).upper().replace('0X', '').replace(' ', '')
        try:
            n = int(v, 16)
            if 0 <= n <= 255:
                return n
        except ValueError:
            pass
        print('  ↑ 请输入 00~FF 的十六进制两位，例如 %s' % (example or '76'))


def from_dump(path):
    """从 1024 字节的 dump 文件里直接读出那 3 个字节"""
    data = open(path, 'rb').read()
    if len(data) < 1024:
        raise ValueError('dump 不足 1024 字节（MIFARE 1K 应为 1024）')
    return data[85], data[86], data[64]        # 日期高、日期低、校验


# ================================================================ 通用内核
# 下面这个函数是"万能改字段"：楼层、园区码、发卡号、房间号、梯号、控制位……
# 全都是同一件事 —— 只要你知道这个字段【现在】的明文，就能算出它的新密文，
# 并同步更新校验码。原理还是"变化量"：同一张卡上，那串密码流在做差时消掉。
#   old_pt/new_pt = 字段的明文（字节串，长度必须相同）
#   old_ct        = 字段现在的密文（从卡上读到的字节串）
#   chk           = 现在的校验码（扇区1 块0 第1字节）
def edit_field(old_pt, new_pt, old_ct, chk):
    if not (len(old_pt) == len(new_pt) == len(old_ct)):
        raise ValueError('明文/新明文/密文的长度必须相同')
    deltas = bytes(bitrev(a ^ b) for a, b in zip(old_pt, new_pt))   # 每个字节的密文增量
    new_ct = bytes(c ^ d for c, d in zip(old_ct, deltas))
    xor = 0
    for d in deltas:
        xor ^= d
    return new_ct, chk ^ xor, deltas


# ================================================================ 楼层（位图）
# 楼层 = 扇区1 块0 第10~16字节，共 7 字节 = 56 位位图，大端存放。
#
# 约定（实测定案的）：
#       **位号 = 楼层号 - 1**，即位 0 = 1 楼，位 N = (N+1) 楼
#   实测过程（同一张卡，逐位写卡 + 用分析软件读楼层列表）：
#       只置位0 -> 软件报"1"      只置位1 -> 报"2"      只置位4 -> 报"5"
#   高位同样验过：只置位8 -> 报"9"，位11 -> "12"，位19 -> "20"，位55 -> "56"。
#   例：明文位图 00 00 00 00 00 07 FE（位 1~10）= 开通 2~11 楼。
#   （约定 A：位 0 = 负一层、位 N = N 楼 —— 已实测排除，用 --base 0 才启用。）
FLOOR_OFF, FLOOR_LEN = 9, 7          # FLOOR_OFF 是"扇区1 内"的偏移，不是文件偏移
S1_ABS = 64                          # 扇区1 在文件里的起始偏移
FLOOR_ABS = S1_ABS + FLOOR_OFF       # 楼层字段在文件里的偏移 = 73


def parse_floors(s):
    """'1-10,15,20' -> {1..10,15,20}；负一层写 -1 或 B1"""
    out = set()
    for part in s.replace('，', ',').replace(' ', '').split(','):
        if not part:
            continue
        if part.upper() in ('B1', '-1', 'B'):
            out.add(-1)
            continue
        if '-' in part.lstrip('-'):
            a, b = part.split('-')
            out.update(range(int(a), int(b) + 1))
        else:
            out.add(int(part))
    return out


def floor_to_bit(f, base=1):
    """楼层号 -> 位号。base=1（默认）: 位0=1楼，位号 = 楼层-1 ; base=0: 位0=负一层，位号=楼层号"""
    if base == 0:
        if f == -1:
            return 0
        if f >= 1:
            return f
        raise ValueError('楼层只支持 B1(-1) 和 1 层以上')
    return f - base


def floors_to_bitmap(floors, base=1):
    v = 0
    for f in floors:
        bit = floor_to_bit(f, base)
        if bit < 0 or bit >= FLOOR_LEN * 8:
            raise ValueError(f'楼层 {f} 超出位图范围')
        v |= 1 << bit
    return v.to_bytes(FLOOR_LEN, 'big')


def bitmap_to_floors(bm, base=1):
    """位图 -> 楼层列表（反着读，用于核对）"""
    v = int.from_bytes(bm, 'big')
    out = []
    for i in range(FLOOR_LEN * 8):
        if (v >> i) & 1:
            out.append(('B1' if (i == 0 and base == 0) else i + base))
    return out


def fmt_floors(fs):
    return ','.join(('B1' if f == 'B1' else str(f)) for f in fs)


def show_floor(old_floors, new_floors, old_ct_hex, chk, base=1):
    old_set, new_set = parse_floors(old_floors), parse_floors(new_floors)
    old_bm = floors_to_bitmap(old_set, base)
    new_bm = floors_to_bitmap(new_set, base)
    old_ct = bytes.fromhex(old_ct_hex.replace(' ', ''))
    new_ct, new_chk, deltas = edit_field(old_bm, new_bm, old_ct, chk)
    print()
    print('-' * 62)
    print(f'  旧楼层 {old_floors}')
    print(f'    位图 = {old_bm.hex(" ").upper()}   （反解：{fmt_floors(bitmap_to_floors(old_bm, base))}）')
    print('  ★ 先核对：反解出来的楼层列表是不是和读卡软件里显示的一样？')
    print('    不一样才需要换约定 -> 加参数 --base 0 再跑一次。')
    print('-' * 62)
    print(f'  新楼层 {new_floors}')
    print(f'    位图 = {new_bm.hex(" ").upper()}   （反解：{fmt_floors(bitmap_to_floors(new_bm, base))}）')
    print('-' * 62)
    print('  ★ 把卡上这 7 个字节改成：')
    print(f'      旧: {old_ct.hex(" ").upper()}')
    print(f'      新: {new_ct.hex(" ").upper()}')
    print(f'      校验码（块0第1字节）: {chk:02X}  ->  {new_chk:02X}')
    print()
    print(f'  （这 7 个字节在扇区1 块0 的第10~16字节，文件偏移 {FLOOR_ABS}~{FLOOR_ABS + FLOOR_LEN - 1}）')
    print('=' * 62)
    return new_ct, new_chk


def show_field(old_pt_hex, new_pt_hex, old_ct_hex, chk):
    old_pt = bytes.fromhex(old_pt_hex.replace(' ', ''))
    new_pt = bytes.fromhex(new_pt_hex.replace(' ', ''))
    old_ct = bytes.fromhex(old_ct_hex.replace(' ', ''))
    new_ct, new_chk, deltas = edit_field(old_pt, new_pt, old_ct, chk)
    print()
    print('-' * 62)
    print(f'  明文 {old_pt.hex(" ").upper()}  ->  {new_pt.hex(" ").upper()}')
    print('-' * 62)
    print('  ★ 把卡上这个字段改成：')
    print(f'      旧密文: {old_ct.hex(" ").upper()}')
    print(f'      新密文: {new_ct.hex(" ").upper()}')
    print(f'      校验码: {chk:02X}  ->  {new_chk:02X}')
    print()
    print('  注意：明文/新明文/密文三者的字节数必须一样，且密文要按"从低地址到高地址"的顺序填。')
    print('=' * 62)
    return new_ct, new_chk


def write_dump(src, dst, old_date, new_date):
    """读 dump -> 改 3 个字节 -> 另存（方便直接拿去写卡）"""
    data = bytearray(open(src, 'rb').read())
    hi, lo, chk = data[85], data[86], data[64]
    nhi, nlo, nchk = calc(old_date, new_date, hi, lo, chk)
    data[85], data[86], data[64] = nhi, nlo, nchk
    open(dst, 'wb').write(data)
    return (hi, lo, chk), (nhi, nlo, nchk)


# ================================================================ 懒人模式：直接改 dump 文件
OFF_CHK, OFF_DHI, OFF_DLO = 64, 85, 86      # 校验码 / 日期高字节 / 日期低字节
MASK_LO_V8 = 0xD4        # 日期低字节的固定掩码（9 张真实卡里 8 张成立）


def read_date_from_dump(data):
    """从 dump 里自动读出到期日的 日 和 月低3位（靠固定掩码 D4）"""
    pt = bitrev(data[OFF_DLO] ^ MASK_LO_V8)
    return pt >> 5, pt & 0x1F          # 月的低3位, 日


_MONTH_DAYS = (31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31)   # 2月按 29 放宽


def is_real_date(m, d):
    return 1 <= m <= 12 and 1 <= d <= _MONTH_DAYS[m - 1]


def month_candidates(m3, day):
    """
    月份的第 4 位（决定 1~8 还是 9~12）在高字节里、被每卡密钥流加密，读不出来。
    m3（月的低3位）只能给出 1~2 个候选，再用"日"筛掉不存在的日期（例如 低3位=4 + 31号 -> 只能 12 月）。
    """
    c = [8] if m3 == 0 else [m3] + ([m3 + 8] if m3 + 8 <= 12 else [])
    ok = [m for m in c if is_real_date(m, day)]
    return ok or c


def fmt_cands(c):
    return '/'.join(str(x) for x in c)


def _month_ok(m3, m):
    """自动读出的月低3位 和 用户报的月份 是否自洽"""
    return (m & 0x07) == m3


def apply_edits(data, edits):
    """一次改多个字段： edits = [(偏移, 旧明文, 新明文), ...]
        每个字节的密文增量 = bitrev(旧明文 ^ 新明文)；校验码增量 = 全部增量的异或"""
    d = bytearray(data)
    total = 0
    for off, old_pt, new_pt in edits:
        if len(old_pt) != len(new_pt):
            raise ValueError('新旧明文字节数不一致')
        for i, (a, b) in enumerate(zip(old_pt, new_pt)):
            delta = bitrev(a ^ b)
            if delta:
                d[off + i] ^= delta
                total ^= delta
    d[OFF_CHK] ^= total
    return d, total


def add_months(y, m, d, n):
    t = (m - 1) + n
    return y + t // 12, t % 12 + 1, d


# ---- 滚动码（扇区10）----
# 事实（都是从真实数据看出来的）：
#   1) 扇区1 和扇区10 在数据上互不影响：把同一张卡改期前后对比，扇区10 一模一样；
#      而卡被电梯刷过一次后，扇区10 的计数器 1->2，扇区1 一个字节都没动。
#      => 改日期/改校验码【不需要】动滚动码，没有发现两者联动。
#   2) 扇区10 是【电梯/读头刷卡时写的】，不是发卡时定的。
#   3) 有些卡的扇区10 本来就是全 0（空白，没被电梯写过），有些已经有记录。
#   4) 已知结构：块1、块2 各是一条记录，形如
#        [计数器1字节] + [3字节值 重复3次] + [夹在中间的2字节]
#      计数器每刷一次 +1，"3字节值"和中间的 2 字节跟着一起变。
#      （看起来是和计数器绑定的加密结果，疑似同一套一卡一密密钥流，但未验证。）
S10_OFF, S10_DATA_LEN = 640, 48      # 扇区10 的三个数据块；尾部 trailer(688~703) 是密钥，不能清


def rolling_state(data):
    """看看扇区10 现在是什么状态"""
    blk = bytes(data[S10_OFF:S10_OFF + S10_DATA_LEN])
    nz = sum(1 for b in blk if b)
    return nz, blk


def clear_rolling(data):
    """清空扇区10 的三个数据块（保留 trailer 里的密钥与存取位）"""
    d = bytearray(data)
    nz, _ = rolling_state(data)
    d[S10_OFF:S10_OFF + S10_DATA_LEN] = b'\x00' * S10_DATA_LEN
    return d, nz


def detect_dump(path):
    """读出这张 dump 里所有能自动得到的信息（不依赖工具、不联网）"""
    data = bytearray(open(path, 'rb').read())
    if len(data) < 1024:
        raise ValueError(f'{path} 不足 1024 字节（MIFARE 1K）')
    uid = bytes(data[:4]).hex().upper()
    key_pred = sector_key(uid)
    m3, dd = read_date_from_dump(data)
    return {
        'data': data,
        'uid': uid,
        'key_pred': key_pred.hex().upper(),
        'key_real': bytes(data[112:118]).hex().upper(),
        'key10': bytes(data[688:694]).hex().upper(),
        'chk': data[OFF_CHK],
        'date_ct': (data[OFF_DHI], data[OFF_DLO]),
        'month3': m3, 'day': dd, 'cands': month_candidates(m3, dd),
        'floor_ct': bytes(data[FLOOR_ABS:FLOOR_ABS + FLOOR_LEN]),
    }


def show_detect(d):
    ok1 = d['key_pred'] == d['key_real']
    ok10 = d['key_pred'] == d['key10']
    print('-' * 62)
    print(f"  文件        : {os.path.basename(d['file'])}")
    print(f"  UID         : {d['uid']}")
    print(f"  扇区密钥推算: {d['key_pred']}   （扇区1 实际 {d['key_real']} {'一致 OK' if ok1 else '不一致'}；"
          f"扇区10 {'一致 OK' if ok10 else d['key10']}）")
    print(f"  校验码      : {d['chk']:02X}        （扇区1 块0 第1字节，偏移 {OFF_CHK}）")
    print(f"  日期密文    : {d['date_ct'][0]:02X} {d['date_ct'][1]:02X}      （块1 第6、7字节）")
    print(f"  自动读出    : 【日 = {d['day']:02d}】（准确）；月份候选 = {fmt_cands(d['cands'])}"
          f"（月份的第4位在高字节里、被每卡密钥流加密，读不出来，只能用「日」筛不存在的日期）")
    print(f"  楼层字段密文: {d['floor_ct'].hex(' ').upper()}   （块0 第10~16字节）")
    if not ok1:
        print('  ⚠️ 密钥推算与实际不符：这张卡可能是别的变种（如 V11 / 新鑫洋V8），请谨慎。')
    print('  ⚠️ 「年」和「楼层」在密文里读不出来（那是每卡一套的密钥流），需要你提供。')
    print('-' * 62)


def lazy_dump(path, out_path=None, cur_date=None, add_years=0, add_months=0,
              set_date=None, cur_floors=None, new_floors=None, clear_roll=None):
    """懒人模式：给一个 dump，检测 + 改期 + 改楼层（可选清滚动码），输出一个新 dump"""
    d = detect_dump(path)
    d['file'] = path
    print()
    print('=' * 62)
    print(' 懒人模式：直接改 dump 文件')
    print('=' * 62)
    show_detect(d)

    # ---- 1) 当前有效期
    if not cur_date:
        guess = f"{datetime.now().year}-{d['cands'][0]:02d}-{d['day']:02d}"   # 年份只是占位，请按实际改
        cur_date = ask(f'① 这张卡现在的有效期（年-月-日）。日={d["day"]:02d} 已自动读出；月份候选={fmt_cands(d["cands"])}（第4位读不出，年份也要你填）',
                       guess)
    y0, m0, dd0 = parse(cur_date)
    if _month_ok(d['month3'], m0) and dd0 == d['day']:
        print(f'   ✓ 自动核对通过：密文里解出的 日={d["day"]:02d}、月低3位={d["month3"]} 与你输入的 {cur_date} 完全自洽')
    else:
        print(f'   ⚠️ 核对不上：密文解出的是 日={d["day"]:02d}、月低3位={d["month3"]}，'
              f'你输入的是 {cur_date}。请确认（或这张卡不是老版 V8，掩码不是 D4）。')

    # ---- 2) 目标日期
    ty, tm, td = y0 + add_years, m0, dd0
    if add_months:
        ty, tm, td = add_months(ty, tm, td, add_months)
    if set_date:
        ty, tm, td = parse(set_date)
    if not (add_years or add_months or set_date):
        v = ask('② 要延时几年？（0 = 不改）', '5')
        add_years = int(v or 0)
        v = ask('③ 再延时几个月？（0 = 不改）', '0')
        add_months = int(v or 0)
        ty, tm, td = y0 + add_years, m0, dd0
        if add_months:
            ty, tm, td = add_months(ty, tm, td, add_months)
        if not (add_years or add_months):
            v = ask('   或者直接给一个目标日期（回车 = 不改日期）', '2031-10-07')
            if v:
                ty, tm, td = parse(v)

    old_v, new_v = pack(y0, m0, dd0), pack(ty, tm, td)
    edits = []
    if old_v != new_v:
        edits.append((OFF_DHI, old_v.to_bytes(2, 'big'), new_v.to_bytes(2, 'big')))
    print()
    print(f'  到期日： {y0}-{m0:02d}-{dd0:02d}  ->  {ty}-{tm:02d}-{td:02d}'
          + ('   （不变）' if old_v == new_v else ''))

    # ---- 3) 楼层
    if cur_floors is None:
        cur_floors = ask('④ 这张卡现在开通哪些楼层？（回车 = 不改楼层；例 2-11）', '2-11')
    if cur_floors:
        if not new_floors:
            new_floors = ask('⑤ 想改成哪些楼层？', '1-11')
        ob = floors_to_bitmap(parse_floors(cur_floors))
        nb = floors_to_bitmap(parse_floors(new_floors))
        edits.append((FLOOR_ABS, ob, nb))
        print(f'  楼层：   {cur_floors}  ->  {new_floors}')
        print(f'          位图 {ob.hex(" ").upper()}  ->  {nb.hex(" ").upper()}')

    if not edits:
        print('\n  没有要改的东西，结束。')
        return None

    # ---- 4) 滚动码（可选）
    nz, _ = rolling_state(d['data'])
    print()
    if nz == 0:
        print('  滚动码（扇区10）：本来就是全 0（空白），无需处理。')
        do_clear = False
    else:
        print(f'  滚动码（扇区10）：里面已有数据（{nz} 个非零字节），通常是电梯刷卡时写进去的。')
        print('     · 不清：保持原样（改日期本来也不影响它）')
        print('     · 清 0：把扇区10 三个数据块清零（密钥保留），让卡看起来像"没被刷过"')
        if clear_roll is None:
            v = ask('⑥ 要不要顺便清空滚动码？（y = 清，其它 = 不清）', 'n')
            do_clear = v.strip().lower() in ('y', 'yes', '是', '1')
        else:
            do_clear = bool(clear_roll)
    if nz and clear_roll:
        do_clear = True
    if nz == 0:
        do_clear = False

    # ---- 5) 落盘
    new_data, total = apply_edits(d['data'], edits)
    cleared = 0
    if do_clear:
        new_data, cleared = clear_rolling(new_data)
    if not out_path:
        base, ext = os.path.splitext(path)
        out_path = f'{base}_改好{ext or ".dump"}'
    open(out_path, 'wb').write(new_data)
    print()
    print('-' * 62)
    print(' 改动明细（只列变了的字节）：')
    for i in range(1024):
        if d['data'][i] != new_data[i]:
            if do_clear and S10_OFF <= i < S10_OFF + S10_DATA_LEN:
                continue
            blk = i // 16
            print(f'   偏移 {i:4d}  (扇区{blk // 4} 块{blk % 4} 第{i % 16 + 1:2d}字节): '
                  f'{d["data"][i]:02X} -> {new_data[i]:02X}')
    print(f'   （校验码总增量 = {total:02X}）')
    if do_clear:
        print(f'   扇区10 三个数据块已清零（原有 {cleared} 个非零字节），密钥/存取位保持不变')
    print(f'  已写出： {out_path}')
    print('=' * 62)
    return out_path


def interactive():
    print('=' * 62)
    print(' 鑫洋V8 电梯卡 计算器')
    print('=' * 62)
    print(' 1) 改有效期（日期码 + 校验码）')
    print(' 2) 由卡号(UID)算扇区密码（用来读卡，不用解卡/嗅探）')
    print(' 3) 改楼层（位图）')
    print(' 4) 改其它字段（园区码 / 发卡号 / 房间号 / 梯号 / 控制位…通用）')
    print(' 5) 懒人模式：直接丢一个 dump 给我，自动检测 + 改期 + 改楼层')
    print()
    c = input(' 请选择 1~5 [5]: ').strip() or '5'
    print()
    if c == '5':
        p = ask('① dump 文件路径（拖进来也行）', 'mycard.dump')
        out = ask('② 输出的新文件名（回车 = 自动命名）', '')
        return lazy_dump(p.strip('"'), out_path=(out.strip('"') or None))
    if c == '2':
        v = ask('卡号 UID（4 字节十六进制，例如 11223344）', '11223344')
        return show_key(v)
    if c == '3':
        old = ask('① 这张卡【现在】开通的楼层', '2-11')
        new = ask('② 想改成（新）哪些楼层', '1-11')
        ct = ask('③ 楼层字段现在的密文 = 扇区1 块0 第10~16字节（7字节）', '1C 93 5E B0 27 84 6A')
        chk = hexbyte('④ 校验码 = 扇区1 块0 第1字节', '80')
        return show_floor(old, new, ct, chk)
    if c == '4':
        old = ask('① 这个字段【现在】的明文（十六进制）', '4E12')
        new = ask('② 想改成的新明文（同样字节数）', '1234')
        ct = ask('③ 这个字段现在的密文（十六进制，同样字节数）', '5E71')
        chk = hexbyte('④ 校验码 = 扇区1 块0 第1字节', '80')
        return show_field(old, new, ct, chk)
    print(' 请先照 README 找到卡上的这 3 个字节（扇区1 里的十六进制值）\n')
    old = ask('① 这张卡【现在】的有效期', '2024-03-15')
    new = ask('② 想改成【新的】有效期', '2029-06-30')
    hi = hexbyte('③ 日期高字节 = 扇区1 块1 第6字节', '76')
    lo = hexbyte('④ 日期低字节 = 扇区1 块1 第7字节', '36')
    chk = hexbyte('⑤ 校验码     = 扇区1 块0 第1字节', '80')
    show(old, new, hi, lo, chk)


# ---------------------------------------------------------------- 功能 2：由 UID 算卡密码
def sector_key(uid_hex):
    """
    鑫洋V8 系列的扇区密码（KeyA = KeyB，扇区1 与扇区10 共用），由卡号直接算出。
    实测：本地 4 张卡 + 论坛 4 张公开卡，共 8 张真实卡全部逐字节命中。
    注意：这是"读卡密码"，不是【功能1】里那串逐字节的加密密钥流。
    """
    h = uid_hex.replace(' ', '').replace(':', '').upper()
    if len(h) != 8:
        raise ValueError('UID 应该是 4 字节 = 8 个十六进制字符，例如 11223344')
    u = [int(h[i:i + 2], 16) for i in (0, 2, 4, 6)]
    return bytes([u[0] ^ 0xFD, u[1] ^ 0x36, u[2] ^ 0x2B,
                  u[3] ^ 0xC3, u[1] ^ 0x41, u[2] ^ 0x68])


def show_key(uid_or_file):
    if os.path.exists(uid_or_file):
        data = open(uid_or_file, 'rb').read()
        uid = data[:4].hex().upper()
        print(f'（从 {uid_or_file} 读到的 UID）')
    else:
        uid = uid_or_file.replace(' ', '').upper()
    k = sector_key(uid)
    print('-' * 62)
    print(f'  卡号 UID : {uid}')
    print(f'  扇区密码 : {k.hex().upper()}   （KeyA = KeyB）')
    print()
    print('  扇区1 与 扇区10 用同一个密码，填进读卡软件就能直接读数据。')
    print('=' * 62)
    return k.hex().upper()


def show(old, new, hi, lo, chk):
    a, b = pack(*parse(old)), pack(*parse(new))
    dh, dl = (a >> 8) ^ (b >> 8), (a & 0xFF) ^ (b & 0xFF)
    nhi, nlo, nchk = calc(old, new, hi, lo, chk)
    print()
    print('-' * 62)
    print(f'  旧日期 {old}  ->  打包值 0x{a:04X}   高 {a >> 8:02X}  低 {a & 0xFF:02X}')
    print(f'  新日期 {new}  ->  打包值 0x{b:04X}   高 {b >> 8:02X}  低 {b & 0xFF:02X}')
    print(f'  变化量:  dh = {dh:02X} -> 倒序 {bitrev(dh):02X}      dl = {dl:02X} -> 倒序 {bitrev(dl):02X}')
    print('-' * 62)
    print('  ★ 把卡上这 3 个字节改成：')
    print()
    print(f'      日期高字节（块1第6字节） : {hi:02X}  ->  {nhi:02X}')
    print(f'      日期低字节（块1第7字节） : {lo:02X}  ->  {nlo:02X}')
    print(f'      校验码    （块0第1字节） : {chk:02X}  ->  {nchk:02X}')
    print()
    print('  其它字节一律不要动。')
    print('=' * 62)


# ---------------------------------------------------------------- 自检
def selftest():
    """自检。用的是虚构的示例卡数据（不是任何真实卡）；公式本身在真实数据上验证过，见 README。"""
    ok = 0

    # 案例 A：示例卡（虚构字节 A3/5C/B0）改 +1 天
    got = calc('2024-03-15', '2024-03-16', 0xA3, 0x5C, 0xB0)
    exp = (0xA3, 0xA4, 0x48)
    print(f'[1] 示例卡 2024-03-15 -> 2024-03-16  得到 {[f"{x:02X}" for x in got]}  期望 {[f"{x:02X}" for x in exp]}  '
          f'{"OK" if got == exp else "FAIL"}')
    ok += got == exp

    # 案例 B：同一张示例卡 -> 2029-06-30（跨年 + 改月）
    got = calc('2024-03-15', '2029-06-30', 0xA3, 0x5C, 0xB0)
    exp = (0xF3, 0xD1, 0x6D)
    print(f'[2] 示例卡 2024-03-15 -> 2029-06-30  得到 {[f"{x:02X}" for x in got]}  期望 {[f"{x:02X}" for x in exp]}  '
          f'{"OK" if got == exp else "FAIL"}')
    ok += got == exp

    # 案例 C：日期打包格式
    print(f'[3] 日期打包: 2024-03-15 -> 0x{pack(2024,3,15):04X} (期望 306F)  '
          f'{"OK" if pack(2024,3,15) == 0x306F else "FAIL"}')
    ok += pack(2024, 3, 15) == 0x306F

    # 案例 D：bit 倒序
    print(f'[4] 倒序: bitrev(0x0A)={bitrev(0x0A):02X} (期望 50)  bitrev(0xB1)={bitrev(0xB1):02X} (期望 8D)  '
          f'{"OK" if bitrev(0x0A) == 0x50 and bitrev(0xB1) == 0x8D else "FAIL"}')
    ok += bitrev(0x0A) == 0x50 and bitrev(0xB1) == 0x8D

    # 案例 E：另一张示例卡，跨年半的改法（2022-12-31 -> 2024-06-06）
    got = calc('2022-12-31', '2024-06-06', 0x77, 0x4B, 0x21)
    print(f'[5] 示例卡 2022-12-31 -> 2024-06-06  得到日期字节 {got[0]:02X} {got[1]:02X}  期望 {0xCF:02X} {0xD1:02X}  '
          f'{"OK" if (got[0], got[1]) == (0xCF, 0xD1) else "FAIL"}')
    ok += (got[0], got[1]) == (0xCF, 0xD1)

    # 案例 F：由 UID 算卡密码（公式在真实卡上验证过；这里用虚构卡号做回归自检）
    okk = 0
    for uid, want in (('11223344', 'EC141887635B'), ('A1B2C3D4', '5C84E817F3AB'),
                      ('0F1E2D3C', 'F22806FF5F45')):
        gotk = sector_key(uid).hex().upper()
        okk += gotk == want
        print(f'[6] 卡号 {uid} -> 密码 {gotk}  期望 {want}  {"OK" if gotk == want else "FAIL"}')
    ok += (okk == 3)

    # 案例 G：楼层位图 —— "位号 = 楼层号 - 1" 这条约定是逐位写卡 + 读楼层列表实测出来的
    bm = floors_to_bitmap(parse_floors('2-11'))
    back = bitmap_to_floors(bm)
    good = (bm.hex().upper() == '000000000007FE') and (back == list(range(2, 12)))
    print(f'[7] 楼层: 2~11 楼 -> 位图 {bm.hex(" ").upper()} （期望 00 00 00 00 00 07 FE）'
          f'  反解 {back[0]}~{back[-1]} 楼  {"OK" if good else "FAIL"}')
    ok += good

    # 案例 H：加开 1 楼（门厅）：2~11 楼 -> 1~11 楼（密文用虚构值）
    old_bm = floors_to_bitmap(parse_floors('2-11'))
    new_bm = floors_to_bitmap(parse_floors('1-11'))
    ct0 = bytes.fromhex('1C935EB027846A')       # 示例卡（虚构）的楼层密文
    nct, nchk, dl = edit_field(old_bm, new_bm, ct0, 0xB0)
    xor = 0
    for x in dl:
        xor ^= x
    good = (nchk == 0xB0 ^ xor) and (bytes(c ^ d for c, d in zip(ct0, dl)) == nct)
    print(f'[8] 楼层 2~11 -> 1~11（加开1楼）  新密文 {nct.hex(" ").upper()}  校验 B0 -> {nchk:02X}  '
          f'{"OK" if good else "FAIL"}')
    ok += good

    # 案例 I：清滚动码只能清数据块，绝不能碰 trailer（密钥/存取位）
    fake = bytearray(1024)
    fake[S10_OFF:S10_OFF + S10_DATA_LEN] = bytes(range(48))
    trailer = bytes.fromhex('EC141887635B' + 'FF078000' + 'EC141887635B')
    fake[688:704] = trailer
    cl, nz = clear_rolling(fake)
    good = (all(b == 0 for b in cl[S10_OFF:S10_OFF + S10_DATA_LEN])
            and bytes(cl[688:704]) == trailer and nz == 47)
    print(f'[9] 清滚动码: 数据块 {nz} 个非零字节已清零，trailer(密钥) 原样保留  '
          f'{"OK" if good else "FAIL"}')
    ok += good

    # 案例 I2：月份候选 —— 月的低3位=4 时 4月/12月 都可能，用「日」筛掉不存在的日期
    ca, cb, cc = month_candidates(4, 31), month_candidates(2, 10), month_candidates(0, 31)
    good = (ca == [12]) and (cb == [2, 10]) and (cc == [8])
    print(f'[10] 月份候选: 低3位=4 日=31 -> {fmt_cands(ca)}   低3位=2 日=10 -> {fmt_cands(cb)}'
          f'   低3位=0 -> {fmt_cands(cc)}   {"OK" if good else "FAIL"}')
    ok += good

    print(f'\n{ok}/10 通过')
    return ok == 10


# ---------------------------------------------------------------- 入口
if __name__ == '__main__':
    args = sys.argv[1:]
    if not args:
        interactive()
    elif args[0] in ('-h', '--help'):
        print(__doc__)
    elif args[0] == '--selftest':
        sys.exit(0 if selftest() else 1)
    elif args[0] == '--key' and len(args) >= 2:
        show_key(args[1])
    elif args[0] == '--floor' and len(args) >= 5:
        base = int(args[args.index('--base') + 1]) if '--base' in args else 1
        show_floor(args[1], args[2], args[3], int(args[4], 16), base)
    elif args[0] == '--field' and len(args) >= 5:
        show_field(args[1], args[2], args[3], int(args[4], 16))
    elif args[0] == 'inspect' and len(args) >= 2:
        _d = detect_dump(args[1]); _d['file'] = args[1]; show_detect(_d)
    elif args[0] == 'dump' and len(args) >= 2:
        kw = {}
        for flag, key in (('--cur-date', 'cur_date'), ('--add-years', 'add_years'),
                          ('--add-months', 'add_months'), ('--set-date', 'set_date'),
                          ('--cur-floors', 'cur_floors'), ('--floors', 'new_floors')):
            if flag in args:
                kw[key] = args[args.index(flag) + 1]
        for k in ('add_years', 'add_months'):
            if k in kw:
                kw[k] = int(kw[k])
        if '--clear-rolling' in args:
            kw['clear_roll'] = True
        out = args[args.index('-o') + 1] if '-o' in args else None
        lazy_dump(args[1], out_path=out, **kw)
    elif len(args) == 5:
        show(args[0], args[1], int(args[2], 16), int(args[3], 16), int(args[4], 16))
    elif len(args) == 3 and args[2].lower().endswith(('.dump', '.mfd', '.bin')):
        (oh, ol, oc), (nh, nl, nc) = write_dump(args[2], 'out_' + args[2], args[0], args[1])
        print(f'已读取 {args[2]}： 校验={oc:02X} 日期高={oh:02X} 日期低={ol:02X}')
        print(f'改成 {args[1]} 后： 校验={nc:02X} 日期高={nh:02X} 日期低={nl:02X}')
        print(f'已生成新文件：out_{args[2]}')
    else:
        print(__doc__)
