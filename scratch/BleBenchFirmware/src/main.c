/*
 * Copyright 2026 Charles Lee
 * SPDX-License-Identifier: PolyForm-Small-Business-1.0.0
 *
 * BLE bench peripheral (docs/patterns/ble-bench-testing.md).
 *
 * Zephyr's Bluetooth shell drives the peripheral: `bt init`, `bt disconnect`, `bt clear`,
 * `bt id-create`. This file adds what the shell does not provide: one advertising set whose
 * address mode the harness chooses, a Heart Rate notification once a second, and a log line for
 * every event a harness waits on. Every such line carries the `bench:` log module prefix.
 *
 * With CONFIG_BENCH_AUTO_ADVERTISE, for a board with no console, there is no shell: the stack
 * comes up at boot and advertises on identity 0, and advertising restarts after every disconnect.
 */

#include <errno.h>
#include <stdlib.h>
#include <string.h>

#include <zephyr/bluetooth/bluetooth.h>
#include <zephyr/bluetooth/conn.h>
#include <zephyr/bluetooth/services/hrs.h>
#include <zephyr/bluetooth/uuid.h>
#include <zephyr/kernel.h>
#include <zephyr/logging/log.h>
#include <zephyr/settings/settings.h>
#include <zephyr/shell/shell.h>

LOG_MODULE_REGISTER(bench, LOG_LEVEL_INF);

/*
 * The advertising set belongs to the system workqueue. Shell commands hand it a request and wait
 * for the result, and the address log runs on the same queue, so no address is read from a set
 * that is being torn down, and a logged address belongs to the set announced before it. The
 * host's own RPA rotation (rpa_timeout) runs there too. HCI traffic is received on the Bluetooth
 * workqueue, so a blocking HCI call from the system workqueue is safe.
 */
static struct bt_le_ext_adv *adv;

static const struct bt_data ad[] = {
	BT_DATA_BYTES(BT_DATA_FLAGS, (BT_LE_AD_GENERAL | BT_LE_AD_NO_BREDR)),
	BT_DATA_BYTES(BT_DATA_UUID16_ALL, BT_UUID_16_ENCODE(BT_UUID_HRS_VAL)),
	BT_DATA(BT_DATA_NAME_COMPLETE, CONFIG_BT_DEVICE_NAME, sizeof(CONFIG_BT_DEVICE_NAME) - 1),
};

/* ── Addresses ──────────────────────────────────────────────────────────── */

static void log_adv_address(struct k_work *work)
{
	ARG_UNUSED(work);

	struct bt_le_ext_adv_info info;
	char addr[BT_ADDR_LE_STR_LEN];

	if (adv == NULL || bt_le_ext_adv_get_info(adv, &info) != 0) {
		return;
	}

	bt_addr_le_to_str(info.addr, addr, sizeof(addr));
	LOG_INF("adv address %s", addr);
}

/*
 * rpa_timeout rotates the RPA after every rpa_expired callback returns, in the same work item,
 * so the new address is read from a work item queued behind it.
 */
static K_WORK_DEFINE(adv_address_work, log_adv_address);

/* ── Advertising ────────────────────────────────────────────────────────── */

static void adv_connected(struct bt_le_ext_adv *instance, struct bt_le_ext_adv_connected_info *info)
{
	ARG_UNUSED(instance);
	ARG_UNUSED(info);

	LOG_INF("adv stopped by connection");
}

#if defined(CONFIG_BT_PRIVACY)
static bool adv_rpa_expired(struct bt_le_ext_adv *instance)
{
	ARG_UNUSED(instance);

	LOG_INF("rpa expired");
	k_work_submit(&adv_address_work);
	return true;
}
#endif

static const struct bt_le_ext_adv_cb adv_cb = {
	.connected = adv_connected,
#if defined(CONFIG_BT_PRIVACY)
	.rpa_expired = adv_rpa_expired,
#endif
};

/* Runs on the system workqueue. */
static void adv_delete(void)
{
	/* A pending log would read the set that replaces this one. */
	(void)k_work_cancel(&adv_address_work);

	if (adv == NULL) {
		return;
	}

	(void)bt_le_ext_adv_stop(adv);
	(void)bt_le_ext_adv_delete(adv);
	adv = NULL;
}

/* Runs on the system workqueue. Returns the failing step through *step. */
static int adv_start(bool identity, uint8_t id, const char **step)
{
	struct bt_le_adv_param param = BT_LE_ADV_PARAM_INIT(
		BT_LE_ADV_OPT_CONN | (identity ? BT_LE_ADV_OPT_USE_IDENTITY : 0),
		BT_GAP_ADV_FAST_INT_MIN_2, BT_GAP_ADV_FAST_INT_MAX_2, NULL);
	int err;

	param.id = id;
	adv_delete();

	*step = "create";
	err = bt_le_ext_adv_create(&param, &adv_cb, &adv);
	if (err) {
		adv = NULL;
		return err;
	}

	*step = "set data";
	err = bt_le_ext_adv_set_data(adv, ad, ARRAY_SIZE(ad), NULL, 0);
	if (!err) {
		*step = "start";
		err = bt_le_ext_adv_start(adv, BT_LE_EXT_ADV_START_DEFAULT);
	}
	if (err) {
		adv_delete();
		return err;
	}

	LOG_INF("adv started mode=%s id=%u", identity ? "identity" : "rpa", id);
	log_adv_address(NULL);
	return 0;
}

/* Shell commands reach the advertising set through a request to the system workqueue. */
#if defined(CONFIG_SHELL)
static struct {
	bool stop;
	bool identity;
	uint8_t id;
	int result;
	const char *step;
} adv_request;

static K_SEM_DEFINE(adv_request_done, 0, 1);

static void adv_request_handler(struct k_work *work)
{
	ARG_UNUSED(work);

	if (adv_request.stop) {
		adv_delete();
		LOG_INF("adv stopped");
		adv_request.result = 0;
	} else {
		adv_request.result = adv_start(adv_request.identity, adv_request.id, &adv_request.step);
	}

	k_sem_give(&adv_request_done);
}

static K_WORK_DEFINE(adv_request_work, adv_request_handler);

/* Called from the shell thread, which is the only caller, so one request is in flight at a time. */
static int adv_request_run(bool stop, bool identity, uint8_t id)
{
	adv_request.stop = stop;
	adv_request.identity = identity;
	adv_request.id = id;
	adv_request.step = "";

	k_work_submit(&adv_request_work);
	k_sem_take(&adv_request_done, K_FOREVER);
	return adv_request.result;
}

static int cmd_adv_start(const struct shell *sh, size_t argc, char **argv)
{
	bool identity = strcmp(argv[1], "identity") == 0;
	uint8_t id = BT_ID_DEFAULT;
	int err;

	if (!identity && strcmp(argv[1], "rpa") != 0) {
		shell_error(sh, "mode must be identity or rpa");
		return -EINVAL;
	}

	if (!identity && !IS_ENABLED(CONFIG_BT_PRIVACY)) {
		shell_error(sh, "rpa needs the privacy image");
		return -ENOTSUP;
	}

	if (argc > 2) {
		id = (uint8_t)strtoul(argv[2], NULL, 0);
	}

	err = adv_request_run(false, identity, id);
	if (err) {
		shell_error(sh, "adv %s failed (%d)", adv_request.step, err);
	}
	return err;
}

static int cmd_adv_stop(const struct shell *sh, size_t argc, char **argv)
{
	ARG_UNUSED(sh);
	ARG_UNUSED(argc);
	ARG_UNUSED(argv);

	return adv_request_run(true, false, 0);
}

static int cmd_status(const struct shell *sh, size_t argc, char **argv)
{
	ARG_UNUSED(argc);
	ARG_UNUSED(argv);

	bt_addr_le_t ids[CONFIG_BT_ID_MAX];
	size_t count = ARRAY_SIZE(ids);
	char addr[BT_ADDR_LE_STR_LEN];

	shell_print(sh, "privacy=%d", IS_ENABLED(CONFIG_BT_PRIVACY));
	bt_id_get(ids, &count);
	for (size_t i = 0; i < count; i++) {
		bt_addr_le_to_str(&ids[i], addr, sizeof(addr));
		shell_print(sh, "id %u %s", (unsigned int)i, addr);
	}

	/* Logs nothing when no set exists; the check runs on the queue that owns the set. */
	k_work_submit(&adv_address_work);
	return 0;
}

SHELL_STATIC_SUBCMD_SET_CREATE(bench_adv_cmds,
	SHELL_CMD_ARG(start, NULL, "<identity|rpa> [id]", cmd_adv_start, 2, 1),
	SHELL_CMD_ARG(stop, NULL, "Stop and delete the advertising set", cmd_adv_stop, 1, 0),
	SHELL_SUBCMD_SET_END);

SHELL_STATIC_SUBCMD_SET_CREATE(bench_cmds,
	SHELL_CMD(adv, &bench_adv_cmds, "Advertising", NULL),
	SHELL_CMD_ARG(status, NULL, "Privacy, identities and advertising address", cmd_status, 1, 0),
	SHELL_SUBCMD_SET_END);

SHELL_CMD_REGISTER(bench, &bench_cmds, "BLE bench peripheral", NULL);
#endif /* CONFIG_SHELL */

/* ── Auto mode ──────────────────────────────────────────────────────────── */

#if defined(CONFIG_BENCH_AUTO_ADVERTISE)
/* Runs on the system workqueue, which owns the advertising set. */
static void auto_advertise(struct k_work *work)
{
	const char *step = "";
	int err;

	ARG_UNUSED(work);
	err = adv_start(true, BT_ID_DEFAULT, &step);
	if (err) {
		LOG_ERR("auto adv %s failed (%d)", step, err);
	}
}

static K_WORK_DEFINE(auto_advertise_work, auto_advertise);

/*
 * Restarting from the disconnected callback can fail while the connection object is still held,
 * and on a single-connection build that leaves the board silent. recycled fires once it is free.
 */
static void recycled(void)
{
	k_work_submit(&auto_advertise_work);
}
#endif /* CONFIG_BENCH_AUTO_ADVERTISE */

/* ── Connections ────────────────────────────────────────────────────────── */

static void peer_str(struct bt_conn *conn, char *buf, size_t len)
{
	bt_addr_le_to_str(bt_conn_get_dst(conn), buf, len);
}

static void connected(struct bt_conn *conn, uint8_t err)
{
	char peer[BT_ADDR_LE_STR_LEN];

	peer_str(conn, peer, sizeof(peer));
	LOG_INF("connected peer=%s err=0x%02x", peer, err);
}

static void disconnected(struct bt_conn *conn, uint8_t reason)
{
	char peer[BT_ADDR_LE_STR_LEN];

	peer_str(conn, peer, sizeof(peer));
	LOG_INF("disconnected peer=%s reason=0x%02x", peer, reason);
}

static void identity_resolved(struct bt_conn *conn, const bt_addr_le_t *rpa, const bt_addr_le_t *identity)
{
	char rpa_str[BT_ADDR_LE_STR_LEN];
	char identity_str[BT_ADDR_LE_STR_LEN];

	ARG_UNUSED(conn);
	bt_addr_le_to_str(rpa, rpa_str, sizeof(rpa_str));
	bt_addr_le_to_str(identity, identity_str, sizeof(identity_str));
	LOG_INF("peer identity resolved rpa=%s identity=%s", rpa_str, identity_str);
}

static void security_changed(struct bt_conn *conn, bt_security_t level, enum bt_security_err err)
{
	ARG_UNUSED(conn);
	LOG_INF("security level=%d err=%d", level, err);
}

BT_CONN_CB_DEFINE(bench_conn_cb) = {
	.connected = connected,
	.disconnected = disconnected,
	.identity_resolved = identity_resolved,
	.security_changed = security_changed,
#if defined(CONFIG_BENCH_AUTO_ADVERTISE)
	.recycled = recycled,
#endif
};

static void pairing_complete(struct bt_conn *conn, bool bonded)
{
	char peer[BT_ADDR_LE_STR_LEN];

	peer_str(conn, peer, sizeof(peer));
	LOG_INF("pairing complete peer=%s bonded=%d", peer, bonded);
}

static void pairing_failed(struct bt_conn *conn, enum bt_security_err reason)
{
	char peer[BT_ADDR_LE_STR_LEN];

	peer_str(conn, peer, sizeof(peer));
	LOG_INF("pairing failed peer=%s reason=%d", peer, reason);
}

static void bond_deleted(uint8_t id, const bt_addr_le_t *peer)
{
	char peer_str_buf[BT_ADDR_LE_STR_LEN];

	bt_addr_le_to_str(peer, peer_str_buf, sizeof(peer_str_buf));
	LOG_INF("bond deleted id=%u peer=%s", id, peer_str_buf);
}

static struct bt_conn_auth_info_cb auth_info_cb = {
	.pairing_complete = pairing_complete,
	.pairing_failed = pairing_failed,
	.bond_deleted = bond_deleted,
};

/* ── Heart Rate ─────────────────────────────────────────────────────────── */

static void hrs_tick(struct k_work *work);
static K_WORK_DELAYABLE_DEFINE(hrs_work, hrs_tick);

static void hrs_tick(struct k_work *work)
{
	static uint8_t bpm = 90;

	ARG_UNUSED(work);

	/* Fails harmlessly before `bt init` and when nobody is subscribed. */
	(void)bt_hrs_notify(bpm);
	bpm = bpm >= 160 ? 90 : bpm + 1;
	k_work_schedule(&hrs_work, K_SECONDS(1));
}

int main(void)
{
	bt_conn_auth_info_cb_register(&auth_info_cb);
	k_work_schedule(&hrs_work, K_SECONDS(1));

	LOG_INF("ready board=%s privacy=%d", CONFIG_BOARD, IS_ENABLED(CONFIG_BT_PRIVACY));

#if defined(CONFIG_BENCH_AUTO_ADVERTISE)
	int err = bt_enable(NULL);

	if (err) {
		LOG_ERR("bt_enable failed (%d)", err);
		return 0;
	}
	settings_load();
	k_work_submit(&auto_advertise_work);
#endif
	return 0;
}
