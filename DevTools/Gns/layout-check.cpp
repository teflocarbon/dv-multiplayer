// Checks the struct offsets GnsNative.cs reads by hand against GameNetworkingSockets' own headers.
// build-gns.ps1 compiles it after building the library; a mismatch fails the build, so a GNS
// update that moves a field can't silently corrupt what the transport reads.
#include <cstddef>
#include <cstdio>
#include <steam/steamnetworkingtypes.h>
#include <steam/isteamnetworkingsockets.h>

static_assert(offsetof(SteamNetworkingMessage_t, m_pData) == 0, "m_pData");
static_assert(offsetof(SteamNetworkingMessage_t, m_cbSize) == 8, "m_cbSize");
static_assert(offsetof(SteamNetworkingMessage_t, m_conn) == 12, "m_conn");
static_assert(offsetof(SteamNetworkingMessage_t, m_nChannel) == 192, "m_nChannel");

static_assert(offsetof(SteamNetConnectionStatusChangedCallback_t, m_hConn) == 0, "m_hConn");
static_assert(offsetof(SteamNetConnectionStatusChangedCallback_t, m_info) == 8, "m_info");
static_assert(offsetof(SteamNetConnectionInfo_t, m_hListenSocket) == 144, "m_hListenSocket");
static_assert(offsetof(SteamNetConnectionInfo_t, m_eState) == 176, "m_eState");
static_assert(offsetof(SteamNetConnectionInfo_t, m_eEndReason) == 180, "m_eEndReason");
static_assert(offsetof(SteamNetConnectionInfo_t, m_szEndDebug) == 184, "m_szEndDebug");
static_assert(offsetof(SteamNetConnectionStatusChangedCallback_t, m_eOldState) == 704, "m_eOldState");

static_assert(offsetof(SteamNetConnectionRealTimeStatus_t, m_nPing) == 4, "m_nPing");
static_assert(sizeof(SteamNetConnectionRealTimeStatus_t) == 120, "SteamNetConnectionRealTimeStatus_t");

static_assert(sizeof(SteamNetworkingIPAddr) == 18, "SteamNetworkingIPAddr");
static_assert(offsetof(SteamNetworkingIPAddr, m_port) == 16, "m_port");

static_assert(k_cbMaxSteamNetworkingSocketsMessageSizeSend == 512 * 1024, "max message size");
static_assert(k_nSteamNetworkingSend_Reliable == 8 && k_nSteamNetworkingSend_Unreliable == 0, "send flags");

static_assert(k_ESteamNetworkingConfig_FakePacketLoss_Send == 2 && k_ESteamNetworkingConfig_FakePacketLoss_Recv == 3, "loss config");
static_assert(k_ESteamNetworkingConfig_FakePacketLag_Send == 4 && k_ESteamNetworkingConfig_FakePacketLag_Recv == 5, "lag config");
static_assert(k_ESteamNetworkingConfig_SendBufferSize == 9, "SendBufferSize");
static_assert(k_ESteamNetworkingConfig_SendRateMin == 10 && k_ESteamNetworkingConfig_SendRateMax == 11, "SendRate");
static_assert(k_ESteamNetworkingConfig_TimeoutConnected == 25, "TimeoutConnected");
static_assert(k_ESteamNetworkingConfig_Int32 == 1 && k_ESteamNetworkingConfig_Float == 3, "config types");
static_assert(k_ESteamNetworkingConnectionState_Connecting == 1 && k_ESteamNetworkingConnectionState_Connected == 3, "states");
static_assert(k_ESteamNetworkingConnectionState_ClosedByPeer == 4 && k_ESteamNetworkingConnectionState_ProblemDetectedLocally == 5, "states");
static_assert(k_ESteamNetConnectionEnd_App_Generic == 1000, "end reasons");

int main()
{
    std::printf("layout ok\n");
    return 0;
}
