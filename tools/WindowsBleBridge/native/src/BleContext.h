#pragma once
#include "wss_ble_windows.h"
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Devices.Bluetooth.h>
#include <winrt/Windows.Devices.Bluetooth.Advertisement.h>
#include <winrt/Windows.Devices.Bluetooth.GenericAttributeProfile.h>
#include <winrt/Windows.Storage.Streams.h>
#include <chrono>
#include <winrt/Windows.Devices.Radios.h>
#include <atomic>
#include <condition_variable>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

namespace wss {
enum class Command { Initialize, QueryAdapter, Start, Stop, Connect, Write, Disconnect };
class BleContext : public std::enable_shared_from_this<BleContext> {
public:
    BleContext(wss_ble_callback callback, void* user) : callback_(callback), user_(user) {}
    ~BleContext();
    void StartWorker();
    int32_t Submit(Command command, uint64_t id, uint64_t session);
    int32_t Cancel(uint64_t id);
    int32_t SubmitGatt(Command command, uint64_t id, uint64_t connection,
        wss_ble_nus_connect_options const* options, uint8_t const* bytes, uint32_t length, uint32_t timeout);
    void Shutdown();
private:
    struct Operation {
        uint64_t id;
        bool cancelled = false; // protected by gate_
        bool ready = false; // reserved completion slot, protected by gate_
        int queryStage = 0; // worker only: 1 = adapter, 2 = radio
        winrt::Windows::Foundation::IAsyncInfo async{ nullptr }; // worker only
        bool gatt = false, finishing = false, ownsConnection = false;
        Command command = Command::Connect;
        uint64_t connection = 0;
        uint32_t stage = 0, gattStatus = UINT32_MAX, protocolError = UINT32_MAX;
        int32_t result = WSS_OK, hr = 0;
        std::chrono::steady_clock::time_point deadline;
        wss_ble_nus_connect_options options{};
        std::string deviceId;
        std::vector<uint8_t> bytes;
    };
    struct Connection {
        uint64_t id = 0;
        bool ready = false, cleaning = false, sawConnected = false, notifyAttached = false, statusAttached = false;
        bool terminal = false;
        int32_t terminalStatus = WSS_NATIVE_FAILURE, terminalHr = 0, cleanupHr = 0;
        uint32_t terminalStage = WSS_LINK_LOSS;
        winrt::Windows::Devices::Bluetooth::BluetoothLEDevice device{ nullptr };
        winrt::Windows::Devices::Bluetooth::GenericAttributeProfile::GattDeviceService service{ nullptr };
        winrt::Windows::Devices::Bluetooth::GenericAttributeProfile::GattSession session{ nullptr };
        winrt::Windows::Devices::Bluetooth::GenericAttributeProfile::GattCharacteristic rx{ nullptr }, tx{ nullptr };
        winrt::event_token valueToken{}, statusToken{};
        winrt::Windows::Foundation::IAsyncInfo cleanup{ nullptr };
        std::chrono::steady_clock::time_point cleanupDeadline;
    };
    struct Notification { uint64_t connection; std::vector<uint8_t> bytes; };
    void ExecuteGatt(const std::shared_ptr<Operation>& op);
    void ProcessGatt(const std::shared_ptr<Operation>& op);
    void AwaitGatt(const std::shared_ptr<Operation>& op, winrt::Windows::Foundation::IAsyncInfo const& async);
    void FinishGatt(const std::shared_ptr<Operation>& op, int32_t status, int32_t hr = 0);
    void AbortGatt(const std::shared_ptr<Operation>& op, int32_t status);
    void BeginCleanup(bool terminal, int32_t status = WSS_NATIVE_FAILURE, uint32_t stage = WSS_LINK_LOSS, int32_t hr = 0);
    void FinishCleanup();
    void TickGatt();
    void ShutdownGatt();
    void Notify(uint64_t connection, winrt::Windows::Storage::Streams::IBuffer const& buffer) noexcept;
    void LinkChanged(uint64_t connection, bool connected) noexcept;
    uint32_t WriteBudget() const;
    void EmitGatt(uint32_t kind, uint64_t operation, uint64_t connection, int32_t status,
        uint32_t stage, uint32_t gattStatus = UINT32_MAX, uint32_t protocolError = UINT32_MAX,
        int32_t hr = 0, std::vector<uint8_t> const* data = nullptr);
    std::shared_ptr<Connection> connection_;
    uint64_t lastConnection_ = 0, writeOperation_ = 0;
    winrt::Windows::Foundation::IAsyncInfo retiredWrite_{ nullptr };
    std::deque<Notification> notifications_; // gate_
    size_t notificationBytes_ = 0;
    uint64_t publishedConnection_ = 0; // gate_
    bool linkPending_ = false, linkConnected_ = false, sawConnectedPending_ = false, notificationOverflow_ = false; // gate_
    struct Advertisement {
        uint64_t session, address;
        uint32_t addressType;
        int32_t rssi;
        bool validRssi;
        std::string name;
        std::vector<uint8_t> uuids;
    };
    void Run();
    void Execute(Command command, const std::shared_ptr<Operation>& op, uint64_t session);
    bool Active(const std::shared_ptr<Operation>& op);
    void Complete(const std::shared_ptr<Operation>& op, int32_t status,
        int32_t hr = 0, uint32_t adapter = WSS_ADAPTER_UNKNOWN, uint64_t session = 0);
    void QueryAdapter(const std::shared_ptr<Operation>& op);
    void ProcessQuery(const std::shared_ptr<Operation>& op);
    void MarkReady(const std::shared_ptr<Operation>& op) noexcept;
    void ScanTerminated(uint64_t session, int32_t status, int32_t hr, uint32_t error) noexcept;
    void StartScan(uint64_t session);
    void StopScan();
    void Received(uint64_t session, winrt::Windows::Devices::Bluetooth::Advertisement::BluetoothLEAdvertisementReceivedEventArgs const& args) noexcept;
    void Emit(wss_ble_event const& event) noexcept;
    void EmitAdvertisement(Advertisement const& item);

    wss_ble_callback callback_;
    void* user_;
    std::mutex gate_;
    std::condition_variable wake_;
    bool closing_ = false;
    std::deque<std::function<void()>> commands_;
    std::deque<Advertisement> advertisements_;
    std::unordered_map<uint64_t, std::shared_ptr<Operation>> operations_;
    uint64_t dropped_ = 0;
    uint64_t publishedSession_ = 0; // protected by gate_, for callback generation checks
    bool terminalPending_ = false;
    wss_ble_event terminal_{}; // reserved watcher-error slot: never allocated in callback
    std::thread worker_;
    int32_t apartmentError_ = 0;
    bool initialized_ = false;
    uint64_t session_ = 0, lastSession_ = 0;
    winrt::Windows::Devices::Bluetooth::Advertisement::BluetoothLEAdvertisementWatcher watcher_{ nullptr };
    winrt::event_token received_{}, stopped_{};
};
int32_t ExceptionStatus() noexcept;
}
