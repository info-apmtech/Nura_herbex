# Tasks and WhatsApp lead assignment — how it works

This document describes how work reaches people in the Nura Herbex admin portal: **tasks**, **WhatsApp leads** and the **"WhatsApp active"** switch. It also records what was fixed in the task board and what to test.

Code references: `TaskService.cs`, `TaskController.cs`, `AdminTasksTab.razor`, `LeadService.cs`, `StaffAvailability.cs`, `FollowUpController.cs`, `AdminWhatsAppToggle.razor`.

---

## 1. Who can do what (access)

Access comes from the person's **designation** (Team → Designations). Each page has a level:

| Page | Level 1 | Level 2 |
|---|---|---|
| Leads & chat, Follow-ups, Tasks, Sales dashboard | **Own records** only | **Everyone's records** |
| Store pages (orders, products…) | View only | View and change |

- People with **no designation** get Level 1 on the four CRM pages (dashboard, leads, follow-ups, tasks).
- Role **admin** can open every page. Role **sales** is the normal staff role.
- Only **sales** staff receive auto-assigned leads and tasks. Admin accounts never do.

---

## 2. Presence: what "online" means

- The notification bell in the top bar asks the server for notifications every **30 seconds**. Each call stamps `LastSeenAt` on the employee.
- A person is **online** if `LastSeenAt` is within the last **2 minutes** (`StaffAvailability.OnlineWindow`).
- Signing out clears `LastSeenAt`, so the person stops receiving work at once.
- Closing the browser without signing out: they drop off within about 2 minutes.

---

## 3. WhatsApp active switch (new)

A switch **"WhatsApp active / off"** sits in the admin top bar, next to the bell. It is stored per employee in `Employees.WhatsAppActive` (default **off**).

**Rules**

1. A new **WhatsApp lead** is only auto-assigned to a person who is: active, role sales, allowed to open the Leads page, **online**, and has **WhatsApp active = on**.
2. Among the people who qualify, the one with the **least open work** gets the lead. Ties go to whoever was given work longest ago.
3. If **nobody qualifies**, the lead stays **unassigned** in the admin queue.
4. Turning the switch **on** also checks the person in as online and **immediately hands over waiting leads** (no wait for the 2-minute worker). The reply message says how many were shared out.
5. Turning it **off** only stops *new* leads. Leads already owned stay with that person.
6. **Leads from other sources** (for example a customer who ordered on the website) are not WhatsApp leads. They go to anyone online and are **not** held back by the switch.
7. Admins can see each person's state in **Team → Employees → WhatsApp** (Active / Off).

### Admin control and automatic switch-off

- **Admin can switch anyone** in the Team > Employees table (the WhatsApp switch in each row; needs the Team page right). Switching **on** needs the person to be signed in and a sales employee, otherwise the system would switch them straight back off. Switching **off** moves their pending leads (below).
- **A person who is "WhatsApp active" but stops being around is switched off automatically.** This happens when they **sign out** (immediately), stop checking in for more than 2 minutes (browser closed, no connection; found by the 2-minute background worker), or are deactivated.
- **Pending leads move to another active person.** Pending = assigned to them, status still **New**, and **no reply from a team member** yet (the automatic welcome message does not count). Each goes to the active, signed-in sales person with the least open work. If nobody is active the lead goes back to the **admin queue** and is assigned as soon as someone switches on.
- Leads already being worked (a team member replied, or the status moved on) **stay** with their owner.
- The same move happens when the person switches themselves off. Each move is written to the lead's assignment history (reason, "auto" or the admin's name).

> Existing staff start with the switch **off**. Until someone turns it on, WhatsApp leads wait in the admin queue.

### Work balance ("open work")
Open work per person = open leads (not converted / not-interested) + open tasks (not done) + overdue follow-ups.

### Settings that affect assignment
- **Auto-assign** (Leads page setting, `leads.autoAssign`): on by default. If an admin turns it off, *no* lead is auto-assigned; everything waits for a manual assignment.

---

## 4. Life of a WhatsApp lead

1. Customer messages the business number. WhatsApp calls `POST /api/webhooks/whatsapp`.
2. `LeadService.UpsertFromWhatsAppAsync` finds the lead by normalised phone number, or creates one (source `whatsapp`, status `new`). Repeat messages update the same lead.
3. `STOP` / `START` keywords set the consent status.
4. `AutoAssignAsync` runs for any lead with no owner (new or older): it picks per rule 3 above and writes a **Lead assignment history** row (`auto`, reason "Auto-assigned (online, WhatsApp active, least busy)").
5. The conversation is stamped (last inbound, 24-hour service window, unread count).
6. **Background worker** (every 2 minutes) runs `AssignWaitingAsync`: gives up to 50 waiting leads, oldest first, to whoever qualifies now. It asks the picker once per kind of lead per pass, so an empty team is not queried 50 times.
7. Staff reply from **Leads & chat**. Sales staff only see their own leads; people with "Everyone's records" see all.

### Manual changes
- **Assign / transfer** (`PUT /api/admin/leads/{id}/assign`): a manager can assign anyone or unassign. A sales owner can hand their lead to another **sales** employee. Every change is written to the history.
- **Employee removed**: their open leads are spread across the remaining qualifying team (same rules) or returned to the admin queue.
- **Follow-up scheduled on an unowned lead**: the person scheduling it takes ownership.

### Known behaviours to be aware of
- A returning customer stays with their existing owner even if that person is now offline or has the switch off. Only *unowned* leads are re-picked.
- If everyone is offline overnight, leads queue up and are shared out within 2 minutes of someone signing in with the switch on (or immediately when they turn it on).

---

## 5. Tasks

### Fields
Title, details, priority (low / normal / high / urgent), due date, status, assignee, department, optional lead and order, tags, repeat (none / daily / weekly / monthly), checklist, comments, activity log.

### Who sees / edits
- **Everyone's records** on Tasks: sees all tasks.
- **Own records**: sees tasks assigned to them or created by them.
- Edit: manager, assignee or creator. Delete: manager or creator.

### Creating
- Pick one or more people: **one separate copy per person**, each tracked and finished on their own.
- **Auto-assign**: least busy person who is online (optionally inside the chosen department). Nobody online = created unassigned.
- Role **templates** pre-fill title, details, priority, repeat, due date and checklist.
- The assignee is notified (unless they created it themselves).

### Statuses
`To do → In progress → Blocked → Done`. Drag cards on the **Board**, use the status box in the task page, or use the tick button in the **List**.

### Marking done (confirmation step)
Pressing **Mark done** opens a confirmation: every checklist step is shown. Any step not ticked needs a **written reason**. The reasons are saved as a comment and in the activity log. The creator is notified.

### Repeating tasks
When a repeating task is marked done, the **next one is created automatically** (next due date; missed occurrences are skipped). The creator/assignee is notified.

### Reminders (background worker, every 2 minutes)
- **Due within 24 hours**: one reminder to the assignee.
- **Overdue**: one notice to the assignee and the creator.
- Changing the due date resets these notices.

### Counts on the chips
| Chip | Meaning |
|---|---|
| Open | Not done |
| Overdue | Not done and past due |
| Due today | Not done and due today |
| Done | Finished in the last **30 days** |
| "My tasks (n)" / "Unassigned (n)" | Open tasks, ignoring the other filters |

---

## 6. What was fixed (task board)

| Problem | Cause | Fix |
|---|---|---|
| After picking **Mark done** and confirming, the task page stayed open | After completing, the screen re-opened the task page | The task page now **closes** after completion and the list refreshes |
| Reopening a done task: **Open count went up but the task did not appear** | If the list was on the *Done* view, the reopened task was filtered out | Reopening from the Done view switches to **Open**, and a message says it is back in open tasks |
| Counts did not match the list | Chips ignored the person / department / priority / search filters; the board showed Done for 14 days but the Done chip counted 30 | Chips now follow the active filters; **one 30-day window** (`DoneWindowDays`) for chip, list and board |
| Reopen then done again **created extra copies** of repeating tasks (count only increased) | Every "done" created a new next occurrence | A new occurrence is not created if one is already open; **reopening removes the untouched next copy** the earlier "done" created |

Verified on a test API (in-memory database): a daily repeating task marked done created one next copy; reopening removed it (open count back to the original); marking done again created exactly one; no duplicates on the board.

---

## 7. Deployment notes

1. Run the database migration `AddEmployeeWhatsAppActive` (adds `Employees.WhatsAppActive`, default false):
   `dotnet ef database update --project src/Nuraherbex.Api`
2. `src/Nuraherbex.Api/Sql/01_schema.sql` is **not** updated; if you create the database from that script, add the column manually:
   `ALTER TABLE Employees ADD WhatsAppActive bit NOT NULL DEFAULT 0;`
3. Tell staff to switch **WhatsApp active** on when they start work.
4. The background worker must be running on exactly one server (`FollowUps:WorkerEnabled`).

---

## 8. Test checklist

**WhatsApp assignment**
- [ ] Staff A online, switch **off**; staff B online, switch **on** → new WhatsApp lead goes to **B**.
- [ ] Both switches off → lead stays **unassigned**; then A turns on → lead is assigned to A within seconds.
- [ ] A and B both on → the one with less open work gets it; next lead goes to the other.
- [ ] A on but signed out → not picked.
- [ ] Auto-assign setting off → nobody gets leads automatically.
- [ ] Admin account with switch on → never auto-assigned.
- [ ] Website-order lead → assigned to an online person even with all switches off.
- [ ] Team > Employees shows the WhatsApp switch correctly; admin can turn a signed-in staff member on and off.
- [ ] Admin tries to turn on someone who is signed out: refused with a clear message.
- [ ] Staff A active with 2 untouched leads and 1 replied lead; A signs out: the 2 untouched leads move to active B, the replied lead stays with A.
- [ ] A closes the browser without signing out: within about 4 minutes A shows Off and untouched leads move.
- [ ] Nobody else active: A's untouched leads go to the admin queue; when someone switches on they are assigned.

**Tasks**
- [ ] Mark done from the list tick, the board button, the board drag to Done and the task-page status box → confirmation appears, then the task page closes and the task leaves Open.
- [ ] Done chip count increases by one; the task shows in Done view and in the board's Done column.
- [ ] Reopen from the Done view → switches to Open; Open count +1 and the task is listed; Done count −1.
- [ ] Repeating task: done → one next copy; reopen → copy removed; done again → one copy.
- [ ] Filter by person: chips change to match the list.
- [ ] Checklist with an unticked step → reason required.
