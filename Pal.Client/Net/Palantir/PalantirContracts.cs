// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026, Puni.sh — 移植自 Palantir（https://github.com/PunishXIV/Palantir）Palantir.Common/Contracts.cs。
// 授權條款與免責聲明全文：同目錄 LICENSE-Palantir.txt。

using System;

namespace Pal.Client.Net.Palantir
{
    // 欄位名與順序對齊伺服器的 Marker；JSON 以不分大小寫讀。
    internal sealed record PalantirMarker(Guid Id, ushort Territory, byte Type, float X, float Y, float Z, int Confirmations = 0);

    internal sealed record PalantirServerMetadata(string Name, string Motd, int ProtocolVersion);

    internal sealed record PalantirAuthRequest(Guid? AccountId);

    internal sealed record PalantirAuthResponse(Guid AccountId, string Token);

    internal static class PalantirProtocol
    {
        /// <summary>伺服器 /metadata 回的版本不等於這個就不連（對方會拒絕舊版）。</summary>
        public const int Version = 2;

        public const byte Trap = 1;
        public const byte Hoard = 2;

        public const float CoordinateLimit = 4096f;

        public static bool IsValidPosition(float x, float y, float z) =>
            float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) &&
            MathF.Abs(x) < CoordinateLimit && MathF.Abs(y) < CoordinateLimit && MathF.Abs(z) < CoordinateLimit;
    }
}
