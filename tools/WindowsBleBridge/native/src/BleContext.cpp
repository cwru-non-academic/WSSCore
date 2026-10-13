#include "BleContext.h"
#include <algorithm>
#include <string>
#include <winrt/base.h>

using namespace winrt;
using namespace Windows::Foundation;
using namespace Windows::Devices::Bluetooth;
using namespace Windows::Devices::Bluetooth::Advertisement;
using namespace Windows::Devices::Radios;

namespace wss {
int32_t ExceptionStatus() noexcept {
    try { throw; }
    catch (hresult_access_denied const&) { return WSS_ACCESS_DENIED; }
    catch (hresult_canceled const&) { return WSS_CANCELLED; }
    catch (...) { return WSS_NATIVE_FAILURE; }
}
BleContext::~BleContext() { Shutdown(); }
void BleContext::StartWorker() { worker_ = std::thread([this] { Run(); }); }
int32_t BleContext::Submit(Command command, uint64_t id, uint64_t session) {
    std::lock_guard<std::mutex> lock(gate_);
    if (closing_) return WSS_CLOSING;
    if (operations_.count(id)) return WSS_INVALID_ARGUMENT;
    if (operations_.size() >= 128) return WSS_BUSY;
    auto op = std::make_shared<Operation>();
    op->id = id;
    // Insert only after allocating the command, so rejection cannot strand an ID.
    commands_.push_back([this, command, op, session] { Execute(command, op, session); });
    try { operations_.emplace(id, op); }
    catch (...) { commands_.pop_back(); throw; }
    wake_.notify_one();
    return WSS_OK;
}
int32_t BleContext::Cancel(uint64_t id) {
    std::lock_guard<std::mutex> lock(gate_);
    if (closing_) return WSS_CLOSING;
    auto found = operations_.find(id);
    if (found == operations_.end()) return WSS_OK; // completion won the race
    auto op = found->second;
    // Cancellation uses the operation's reserved slot, not another allocated command.
    op->cancelled = true;
    wake_.notify_one();
    return WSS_OK;
}
bool BleContext::Active(const std::shared_ptr<Operation>& op) {
    std::lock_guard<std::mutex> lock(gate_);
    auto found = operations_.find(op->id);
    return !closing_ && found != operations_.end() && found->second == op;
}
void BleContext::Complete(const std::shared_ptr<Operation>& op, int32_t status, int32_t hr, uint32_t adapter, uint64_t session) {
    {
        std::lock_guard<std::mutex> lock(gate_);
        auto found = operations_.find(op->id);
        if (closing_ || found == operations_.end() || found->second != op) return;
        if (op->cancelled) status = WSS_CANCELLED;
        operations_.erase(found);
    }
    op->async = nullptr; // break operation/delegate cycles, on the MTA worker
    wss_ble_event event{ sizeof(wss_ble_event), WSS_COMPLETION, op->id, session, status, hr, 0, adapter, nullptr, 0 };
    Emit(event);
}
void BleContext::Execute(Command command, const std::shared_ptr<Operation>& op, uint64_t session) {
    if (!Active(op)) return;
    {
        std::lock_guard<std::mutex> lock(gate_);
        if (op->cancelled && command != Command::Stop) {
            // Cancel command follows this command and publishes its terminal result.
            return;
        }
    }
    try {
        if (apartmentError_) { Complete(op, WSS_NATIVE_FAILURE, apartmentError_); return; }
        if (command == Command::Initialize) { initialized_ = true; Complete(op, WSS_OK); return; }
        if (!initialized_) { Complete(op, WSS_INVALID_STATE); return; }
        if (command == Command::QueryAdapter) { QueryAdapter(op); return; }
        if (command == Command::Start) {
            if (session_ || session <= lastSession_) { Complete(op, WSS_INVALID_STATE); return; }
            StartScan(session);
        } else {
            if (session_ && session_ != session) { Complete(op, WSS_INVALID_STATE); return; }
            StopScan();
        }
        Complete(op, WSS_OK, 0, WSS_ADAPTER_UNKNOWN, session);
    } catch (...) {
        auto status = ExceptionStatus();
        auto hr = to_hresult().value;
        if (command == Command::Start) StopScan();
        Complete(op, status, hr, WSS_ADAPTER_UNKNOWN, session);
    }
}
void BleContext::QueryAdapter(const std::shared_ptr<Operation>& op) {
    auto async = BluetoothAdapter::GetDefaultAsync();
    op->async = async;
    op->queryStage = 1;
    auto weak = weak_from_this();
    async.Completed([weak, op](auto const&, AsyncStatus) noexcept {
        if (auto self = weak.lock()) self->MarkReady(op);
    });
}
void BleContext::MarkReady(const std::shared_ptr<Operation>& op) noexcept {
    // Storage was reserved when accepting the operation: no function/deque allocation
    // occurs at a WinRT completion boundary, even when advertisements saturate the queue.
    try {
        std::lock_guard<std::mutex> lock(gate_);
        auto found = operations_.find(op->id);
        if (!closing_ && found != operations_.end() && found->second == op) {
            op->ready = true;
            wake_.notify_one();
        }
    } catch (...) {}
}
void BleContext::ProcessQuery(const std::shared_ptr<Operation>& op) {
    if (!Active(op)) return;
    try {
        if (op->queryStage == 1) {
            auto adapter = op->async.as<IAsyncOperation<BluetoothAdapter>>().GetResults();
            if (!adapter) { Complete(op, WSS_OK, 0, WSS_ADAPTER_ABSENT); return; }
            if (!adapter.IsLowEnergySupported()) { Complete(op, WSS_OK, 0, WSS_ADAPTER_NO_LE); return; }
            auto radioAsync = adapter.GetRadioAsync();
            op->async = radioAsync;
            op->queryStage = 2;
            auto weak = weak_from_this();
            radioAsync.Completed([weak, op](auto const&, AsyncStatus) noexcept {
                if (auto self = weak.lock()) self->MarkReady(op);
            });
        } else {
            auto radio = op->async.as<IAsyncOperation<Radio>>().GetResults();
            uint32_t state = WSS_ADAPTER_UNKNOWN;
            if (radio) state = radio.State() == RadioState::On ? WSS_ADAPTER_READY : WSS_ADAPTER_OFF;
            Complete(op, WSS_OK, 0, state);
        }
    } catch (...) { Complete(op, ExceptionStatus(), to_hresult().value); }
}
void BleContext::ScanTerminated(uint64_t session, int32_t status, int32_t hr, uint32_t error) noexcept {
    try {
        std::lock_guard<std::mutex> lock(gate_);
        if (closing_ || publishedSession_ != session || terminalPending_) return;
        terminal_ = { sizeof(wss_ble_event), WSS_SCAN_TERMINATED, 0, session, status, hr, error, 0, nullptr, 0 };
        terminalPending_ = true;
        wake_.notify_one();
    } catch (...) {}
}
void BleContext::StartScan(uint64_t session) {
    watcher_ = BluetoothLEAdvertisementWatcher();
    watcher_.ScanningMode(BluetoothLEScanningMode::Active);
    auto weak = weak_from_this();
    received_ = watcher_.Received([weak, session](auto const&, auto const& args) noexcept {
        if (auto owner = weak.lock()) owner->Received(session, args);
    });
    stopped_ = watcher_.Stopped([weak, session](auto const&, auto const& args) noexcept {
        try {
            auto error = args.Error();
            if (auto owner = weak.lock()) {
                int32_t status = WSS_NATIVE_FAILURE;
                if (error == BluetoothError::RadioNotAvailable) status = WSS_ADAPTER_UNAVAILABLE;
                else if (error == BluetoothError::DisabledByPolicy || error == BluetoothError::ConsentRequired) status = WSS_ACCESS_DENIED;
                owner->ScanTerminated(session, status, 0, static_cast<uint32_t>(error));
            }
        } catch (...) { if (auto owner = weak.lock()) owner->ScanTerminated(session, WSS_NATIVE_FAILURE, to_hresult().value, 0); }
    });
    session_ = lastSession_ = session;
    { std::lock_guard<std::mutex> lock(gate_); publishedSession_ = session; }
    watcher_.Start();
}
void BleContext::StopScan() {
    session_ = 0; // fence queued callbacks before touching the watcher
    bool reportTerminal = false;
    wss_ble_event pendingTerminal{};
    {
        std::lock_guard<std::mutex> lock(gate_);
        advertisements_.clear();
        dropped_ = 0;
        publishedSession_ = 0;
        reportTerminal = terminalPending_;
        pendingTerminal = terminal_;
        terminalPending_ = false;
    }
    if (watcher_) {
        try { watcher_.Received(received_); } catch (...) {}
        try { watcher_.Stopped(stopped_); } catch (...) {}
        try { watcher_.Stop(); } catch (...) {}
        watcher_ = nullptr;
    }
    // An unexpected termination that raced an explicit stop is still reported before
    // that stop's completion. Expected Stopped events after revocation are suppressed.
    if (reportTerminal) Emit(pendingTerminal);
}
void BleContext::Received(uint64_t session, BluetoothLEAdvertisementReceivedEventArgs const& args) noexcept {
    try {
        if (args.IsAnonymous() || !args.BluetoothAddress()) return;
        Advertisement item{};
        item.session = session;
        item.address = args.BluetoothAddress();
        auto type = args.BluetoothAddressType();
        item.addressType = type == BluetoothAddressType::Public ? WSS_ADDRESS_PUBLIC :
            type == BluetoothAddressType::Random ? WSS_ADDRESS_RANDOM : WSS_ADDRESS_UNSPECIFIED;
        item.rssi = args.RawSignalStrengthInDBm();
        item.validRssi = item.rssi != -127;
        item.name = to_string(args.Advertisement().LocalName());
        for (auto const& uuid : args.Advertisement().ServiceUuids()) {
            // GUID scalar fields become canonical big-endian UUID bytes.
            item.uuids.insert(item.uuids.end(), {
                static_cast<uint8_t>(uuid.Data1 >> 24), static_cast<uint8_t>(uuid.Data1 >> 16),
                static_cast<uint8_t>(uuid.Data1 >> 8), static_cast<uint8_t>(uuid.Data1),
                static_cast<uint8_t>(uuid.Data2 >> 8), static_cast<uint8_t>(uuid.Data2),
                static_cast<uint8_t>(uuid.Data3 >> 8), static_cast<uint8_t>(uuid.Data3) });
            item.uuids.insert(item.uuids.end(), std::begin(uuid.Data4), std::end(uuid.Data4));
        }
        std::lock_guard<std::mutex> lock(gate_);
        if (closing_ || publishedSession_ != session) return;
        if (advertisements_.size() >= 256) ++dropped_;
        else advertisements_.push_back(std::move(item));
        wake_.notify_one();
    } catch (...) {
        ScanTerminated(session, WSS_NATIVE_FAILURE, to_hresult().value, 0);
    }
}
void BleContext::Emit(wss_ble_event const& event) noexcept {
    {
        std::lock_guard<std::mutex> lock(gate_);
        if (closing_) return;
    }
    try { callback_(user_, &event); } catch (...) {} // caller must also contain managed exceptions
}
void BleContext::EmitAdvertisement(Advertisement const& item) {
    if (item.session != session_) return;
    wss_ble_advertisement payload{ sizeof(wss_ble_advertisement), item.addressType, item.address,
        item.rssi, item.validRssi ? 1u : 0u, reinterpret_cast<const uint8_t*>(item.name.data()),
        static_cast<uint32_t>(item.name.size()), static_cast<uint32_t>(item.uuids.size() / 16), item.uuids.data() };
    wss_ble_event event{ sizeof(wss_ble_event), WSS_ADVERTISEMENT, 0, item.session, WSS_OK, 0, 0, 0, &payload, 0 };
    Emit(event);
}
void BleContext::Run() {
    bool apartment = false;
    try { init_apartment(apartment_type::multi_threaded); apartment = true; }
    catch (...) { apartmentError_ = to_hresult().value; }
    for (;;) {
        std::function<void()> action;
        Advertisement item{};
        bool hasItem = false;
        bool hasTerminal = false;
        wss_ble_event terminal{};
        std::shared_ptr<Operation> ready;
        bool cancelled = false;
        uint64_t dropped = 0;
        {
            std::unique_lock<std::mutex> lock(gate_);
            wake_.wait_for(lock, std::chrono::milliseconds(10), [this] {
                if (closing_ || !commands_.empty() || !advertisements_.empty() || dropped_ || terminalPending_ ||
                    (!notifications_.empty() && connection_ && connection_->ready) || linkPending_ || notificationOverflow_) return true;
                for (auto const& pair : operations_) if (!pair.second->finishing && (pair.second->ready || pair.second->cancelled)) return true;
                return false;
            });
            if (closing_) break;
            if (!commands_.empty()) { action = std::move(commands_.front()); commands_.pop_front(); }
            else {
                for (auto const& pair : operations_) {
                    if (!pair.second->finishing && (pair.second->ready || pair.second->cancelled)) {
                        ready = pair.second; ready->ready = false; cancelled = ready->cancelled; break;
                    }
                }
                if (!ready) {
                    if (terminalPending_) { terminal = terminal_; terminalPending_ = false; hasTerminal = true; }
                    else {
                        dropped = dropped_; dropped_ = 0;
                        if (!advertisements_.empty()) { item = std::move(advertisements_.front()); advertisements_.pop_front(); hasItem = true; }
                    }
                }
            }
        }
        try {
            if (action) action();
            else if (ready) {
                if (ready->gatt) {
                    if (cancelled) AbortGatt(ready, WSS_CANCELLED);
                    else ProcessGatt(ready);
                } else if (cancelled) {
                    try { if (ready->async) ready->async.Cancel(); } catch (...) {}
                    Complete(ready, WSS_CANCELLED);
                } else ProcessQuery(ready);
            }
            else if (hasTerminal) {
                if (terminal.session_id == session_) { StopScan(); Emit(terminal); }
            }
            else {
                if (dropped && session_) {
                    wss_ble_event event{ sizeof(wss_ble_event), WSS_SCAN_OVERFLOW, 0, session_, WSS_OVERFLOW, 0, 0, 0, nullptr, dropped };
                    Emit(event);
                }
                if (hasItem) EmitAdvertisement(item);
            }
            TickGatt();
        } catch (...) {} // no worker exception can cross the thread entry point
    }
    StopScan();
    ShutdownGatt();
    // No callbacks after closing_. Async handlers capture only weak context ownership.
    // Releasing this map does not wait for WinRT cancellation or result callbacks.
    std::unordered_map<uint64_t, std::shared_ptr<Operation>> pending;
    {
        std::lock_guard<std::mutex> lock(gate_);
        pending.swap(operations_);
        commands_.clear();
    }
    for (auto const& entry : pending) {
        try { if (entry.second->async) { entry.second->async.Cancel(); entry.second->async.Close(); } } catch (...) {}
        entry.second->async = nullptr;
    }
    pending.clear();
    if (apartment) uninit_apartment();
}
void BleContext::Shutdown() {
    {
        std::lock_guard<std::mutex> lock(gate_);
        closing_ = true;
        wake_.notify_all();
    }
    if (worker_.joinable()) worker_.join();
}
}
