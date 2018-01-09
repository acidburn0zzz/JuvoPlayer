﻿using JuvoPlayer.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace JuvoPlayer.DRM
{
    public interface IDRMSession
    {
        string CurrentDrmScheme { get; }
        StreamPacket DecryptPacket(StreamPacket packet);
        void UpdateSession(DRMInitData drmInitData);
        void SetDrmConfiguration(DRMDescription drmDescription);
    }
}
