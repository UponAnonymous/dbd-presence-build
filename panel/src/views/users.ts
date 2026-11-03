// Users (FR-ADM-07, FR-ADM-09): list with status, access, devices, uploads, created and last seen; revoke a user or one
// device; block or allow a user's new uploads; delete a user after typed confirmation; export everything held about a
// user as a JSON download (the export is audited).

import type { Device, User } from "../api.ts";
import type { View, ViewContext } from "../context.ts";
import { el, icon, replaceChildren } from "../dom.ts";
import { describeError } from "../errors.ts";
import { date, plural, relative, shortId } from "../format.ts";
import { badge, button, card, cell, emptyState, errorState, loading, mono, notice, pageHeader, setBusy, stacked, table } from "../ui.ts";

const COLUMNS = ["User", "Status", "Access", "Devices", "Created", "Last seen", "Actions"] as const;

function accessText(user: User): { label: string; detail: string | null } {
  if (user.access === "invite_required") return { label: "Waiting for an invite", detail: null };
  const via = user.accessVia === "invite" ? "With an invite" : user.accessVia === "public" ? "Public sign-up" : user.accessVia;
  return { label: "Granted", detail: via };
}

function activeDevices(user: User): number {
  return user.devices.filter((d) => d.revokedAt === null).length;
}

function render(container: HTMLElement, ctx: ViewContext): void {
  let users: User[] = [];
  const expanded = new Set<string>();
  const listBody = el("div", { className: "card-body" });
  const listMessage = el("div", { className: "inline-message" });
  const refresh = button("Refresh", { icon: "refresh", onClick: () => void load() });
  const summary = el("p", { className: "card-summary muted" });

  replaceChildren(
    container,
    pageHeader("Users", "Everyone who has signed in with Discord. Revoke access, end a device's sessions, export a user's data or delete their account.", refresh),
    card("All users", summary, listMessage, listBody),
  );

  async function load(): Promise<void> {
    replaceChildren(listMessage);
    summary.textContent = "";
    replaceChildren(listBody, loading("Loading users…"));
    setBusy(refresh, true);
    try {
      users = await ctx.api.listUsers();
      draw();
    } catch (error) {
      ctx.reportError(error);
      replaceChildren(listBody, errorState(error, () => void load(), ctx.reload));
    } finally {
      setBusy(refresh, false);
    }
  }

  function showFailure(prefix: string, error: unknown): void {
    ctx.reportError(error);
    const copy = describeError(error);
    replaceChildren(listMessage, notice("danger", `${prefix}: ${copy.title}`, copy.detail, { live: true }));
  }

  function draw(): void {
    if (users.length === 0) {
      summary.textContent = "";
      replaceChildren(listBody, emptyState("No users yet", "People appear here after they sign in with Discord in the app."));
      return;
    }
    const active = users.filter((u) => u.status === "active").length;
    summary.textContent = `${plural(users.length, "user", "users")} · ${active} active · ${users.length - active} revoked`;
    const rows: HTMLTableRowElement[] = [];
    for (const user of users) {
      rows.push(userRow(user));
      if (expanded.has(user.id)) rows.push(deviceRow(user));
    }
    replaceChildren(listBody, table("Users, newest first", COLUMNS, rows, "table-users"));
  }

  function userRow(user: User): HTMLTableRowElement {
    const access = accessText(user);
    const activeCount = activeDevices(user);
    const isOpen = expanded.has(user.id);
    const devicesToggle =
      user.devices.length === 0
        ? el("span", { className: "muted", text: "None" })
        : el(
            "button",
            {
              className: "disclosure",
              attrs: { type: "button", "aria-expanded": isOpen ? "true" : "false", "aria-controls": `devices-${user.id}`, "data-devices": user.id },
              on: {
                click: () => {
                  if (expanded.has(user.id)) expanded.delete(user.id);
                  else expanded.add(user.id);
                  draw();
                  container.querySelector<HTMLButtonElement>(`[data-devices="${user.id}"]`)?.focus();
                },
              },
            },
            icon("chevron", "icon disclosure-icon"),
            el("span", { text: user.devices.length > activeCount ? `${activeCount} active of ${user.devices.length}` : `${activeCount} active` }),
          );

    const actions = el(
      "div",
      { className: "row-actions" },
      button("Export", { small: true, icon: "download", attrs: { "aria-label": `Export data for ${user.displayName}` }, onClick: (e) => void exportUser(user, e.currentTarget as HTMLButtonElement) }),
      button(user.uploadsBlocked ? "Allow uploads" : "Block uploads", {
        small: true,
        attrs: { "aria-label": `${user.uploadsBlocked ? "Allow" : "Block"} uploads for ${user.displayName}` },
        onClick: (e) => void setUploads(user, !user.uploadsBlocked, e.currentTarget as HTMLButtonElement),
      }),
      user.status === "active"
        ? button("Revoke", { small: true, variant: "danger", attrs: { "aria-label": `Revoke ${user.displayName}` }, onClick: (e) => void revokeUser(user, e.currentTarget as HTMLButtonElement) })
        : null,
      button("Delete", { small: true, variant: "danger", attrs: { "aria-label": `Delete ${user.displayName}` }, onClick: (e) => void deleteUser(user, e.currentTarget as HTMLButtonElement) }),
    );

    return el(
      "tr",
      { attrs: { "data-user": user.id } },
      cell("User", stacked(user.displayName, mono(`ID ${shortId(user.id)}`, "subtle"))),
      cell("Status", user.status === "active" ? badge("success", "Active") : stacked(badge("danger", "Revoked"), user.revokedAt ? date(user.revokedAt) : null)),
      cell("Access", stacked(access.label === "Granted" ? badge("info", "Granted") : badge("warning", access.label), access.detail)),
      cell("Devices", stacked(devicesToggle, `${plural(user.uploads, "upload", "uploads")}${user.uploadsBlocked ? " · uploads blocked" : ""}`)),
      cell("Created", date(user.createdAt)),
      cell("Last seen", stacked(relative(user.lastSeenAt, ctx.now()), date(user.lastSeenAt))),
      cell("Actions", actions),
    );
  }

  function deviceRow(user: User): HTMLTableRowElement {
    const items = user.devices.map((device) => deviceItem(user, device));
    return el(
      "tr",
      { className: "detail-row" },
      el(
        "td",
        { attrs: { colspan: COLUMNS.length, id: `devices-${user.id}` } },
        el("p", { className: "detail-title", text: `Devices for ${user.displayName}` }),
        el("ul", { className: "device-list", attrs: { "aria-label": `Devices for ${user.displayName}` } }, ...items),
      ),
    );
  }

  function deviceItem(user: User, device: Device): HTMLLIElement {
    const revoked = device.revokedAt !== null;
    return el(
      "li",
      { className: "device-item" },
      el("div", { className: "device-key" }, el("span", { className: "device-label", text: "Device key" }), mono(device.thumbprint, "wrap")),
      el("div", { className: "device-meta" }, el("span", { text: `Added ${date(device.createdAt)}` }), el("span", { text: `Last seen ${relative(device.lastSeenAt, ctx.now())}` })),
      el(
        "div",
        { className: "device-status" },
        revoked ? stacked(badge("danger", "Revoked"), device.revokedAt ? date(device.revokedAt) : null) : badge("success", "Active"),
        revoked
          ? null
          : button("Revoke device", {
              small: true,
              variant: "danger",
              attrs: { "aria-label": `Revoke device ${device.thumbprint.slice(0, 8)} of ${user.displayName}` },
              onClick: (e) => void revokeDevice(user, device, e.currentTarget as HTMLButtonElement),
            }),
      ),
    );
  }

  async function exportUser(user: User, trigger: HTMLButtonElement): Promise<void> {
    setBusy(trigger, true, "Exporting…");
    try {
      const data = await ctx.api.exportUser(user.id);
      const day = new Date(ctx.now()).toISOString().slice(0, 10);
      ctx.download(`dbd-presence-user-${shortId(user.id)}-${day}.json`, data);
      replaceChildren(listMessage);
      ctx.announce(`Export for ${user.displayName} downloaded. The export is recorded in the audit log.`);
    } catch (error) {
      showFailure("Could not export the data", error);
    } finally {
      setBusy(trigger, false);
    }
  }

  async function setUploads(user: User, blocked: boolean, trigger: HTMLButtonElement): Promise<void> {
    if (blocked) {
      const ok = await ctx.confirm({
        title: `Block uploads for ${user.displayName}?`,
        body: ["Their new uploads are refused. Images they already uploaded stay online; take them down in Uploads if needed."],
        confirmLabel: "Block uploads",
        danger: true,
      });
      if (!ok) return;
    }
    setBusy(trigger, true, "Saving…");
    try {
      const uploadsBlocked = await ctx.api.setUserUploadBlock(user.id, blocked);
      users = users.map((u) => (u.id === user.id ? { ...u, uploadsBlocked } : u));
      replaceChildren(listMessage);
      draw();
      ctx.announce(uploadsBlocked ? `Uploads blocked for ${user.displayName}.` : `Uploads allowed for ${user.displayName}.`);
    } catch (error) {
      setBusy(trigger, false);
      showFailure("Could not change the user's uploads", error);
    }
  }

  async function revokeUser(user: User, trigger: HTMLButtonElement): Promise<void> {
    const ok = await ctx.confirm({
      title: `Revoke ${user.displayName}?`,
      body: [
        "Their sessions end on every device and they cannot sign in again.",
        "Their data stays until you delete the user. Revoking cannot be undone from the panel.",
      ],
      confirmLabel: "Revoke user",
      danger: true,
    });
    if (!ok) return;
    setBusy(trigger, true, "Revoking…");
    try {
      const revokedAt = await ctx.api.revokeUser(user.id);
      users = users.map((u) => (u.id === user.id ? { ...u, status: "revoked", revokedAt } : u));
      replaceChildren(listMessage);
      draw();
      ctx.announce(`${user.displayName} revoked.`);
    } catch (error) {
      setBusy(trigger, false);
      showFailure("Could not revoke the user", error);
    }
  }

  async function revokeDevice(user: User, device: Device, trigger: HTMLButtonElement): Promise<void> {
    const ok = await ctx.confirm({
      title: "Revoke this device?",
      body: [
        `Device key ${device.thumbprint} of ${user.displayName}.`,
        "Its sessions end at once and this device key can never sign in again. The user keeps access on their other devices, and this device no longer counts towards their device limit.",
      ],
      confirmLabel: "Revoke device",
      danger: true,
    });
    if (!ok) return;
    setBusy(trigger, true, "Revoking…");
    try {
      const revokedAt = await ctx.api.revokeDevice(user.id, device.id);
      users = users.map((u) => (u.id === user.id ? { ...u, devices: u.devices.map((d) => (d.id === device.id ? { ...d, revokedAt } : d)) } : u));
      replaceChildren(listMessage);
      draw();
      container.querySelector<HTMLButtonElement>(`[data-devices="${user.id}"]`)?.focus();
      ctx.announce("Device revoked.");
    } catch (error) {
      setBusy(trigger, false);
      showFailure("Could not revoke the device", error);
    }
  }

  async function deleteUser(user: User, trigger: HTMLButtonElement): Promise<void> {
    const ok = await ctx.confirm({
      title: `Delete ${user.displayName}?`,
      body: [
        "This permanently deletes the account, its devices, sessions and uploaded images. It cannot be undone. Export the data first if you need a copy.",
        "Audit entries about this user stay, linked only to the random user ID.",
      ],
      confirmLabel: "Delete user",
      danger: true,
      typed: { expected: shortId(user.id), label: "To confirm, type the user's short ID" },
    });
    if (!ok) return;
    setBusy(trigger, true, "Deleting…");
    try {
      const filesRemoved = await ctx.api.deleteUser(user.id);
      users = users.filter((u) => u.id !== user.id);
      expanded.delete(user.id);
      replaceChildren(listMessage);
      draw();
      if (!filesRemoved) {
        replaceChildren(
          listMessage,
          notice("warning", "Deleted, but some uploaded files are not deleted yet", "Their addresses no longer serve the images. Deleting the files from storage failed; the service retries daily.", { live: true }),
        );
      }
      container.querySelector<HTMLElement>("#page-title")?.focus();
      ctx.announce(`${user.displayName} deleted.`);
    } catch (error) {
      setBusy(trigger, false);
      showFailure("Could not delete the user", error);
    }
  }

  void load();
}

export const usersView: View = { render };
