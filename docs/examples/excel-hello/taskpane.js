// The task pane: opens the sign-in dialog, receives the authorization code from it, trades it for tokens and asks the home who this is.
// Nothing is stored: the token lives in this page's memory (a closed pane signs out). See docs/excel-addin-spike.md.
const who = document.getElementById("who");
const problem = document.getElementById("problem");
const signIn = document.getElementById("sign-in");
const signOut = document.getElementById("sign-out");
let token = null;

function show(text) { who.textContent = text; }
function fail(text) { problem.textContent = text; }

async function exchange(code, verifier) {
  const { home, clientId } = window.HELLO;
  const body = new URLSearchParams({
    grant_type: "authorization_code", code, client_id: clientId,
    redirect_uri: location.origin + "/auth-done.html", code_verifier: verifier,
  });
  const r = await fetch(home + "/identity/token", { method: "POST", body });
  if (!r.ok) throw new Error("The home refused the code (" + r.status + "): " + await r.text());
  return (await r.json()).access_token;
}

async function showUser() {
  const r = await fetch(window.HELLO.home + "/identity/userinfo", { headers: { authorization: "Bearer " + token } });
  if (!r.ok) throw new Error("userinfo answered " + r.status);
  const u = await r.json();
  show("Signed in as " + (u.name || u.preferred_username || u.email || u.sub) + (u.email ? " (" + u.email + ")" : ""));
  signOut.hidden = false;
}

function begin() {
  fail("");
  Office.context.ui.displayDialogAsync(location.origin + "/dialog.html", { height: 60, width: 30 }, (opened) => {
    if (opened.status !== Office.AsyncResultStatus.Succeeded) { fail("The dialog did not open: " + opened.error.message + " (code " + opened.error.code + ")"); return; }
    const dialog = opened.value;
    dialog.addEventHandler(Office.EventType.DialogMessageReceived, async (arg) => {
      dialog.close();
      try {
        const m = JSON.parse(arg.message);
        if (m.error) throw new Error(m.error);
        token = await exchange(m.code, m.verifier);
        await showUser();
      } catch (e) { fail(String(e.message || e)); }
    });
    dialog.addEventHandler(Office.EventType.DialogEventReceived, (arg) => fail("The dialog ended (code " + arg.error + ")."));
  });
}

Office.onReady((info) => {
  if (info.host !== Office.HostType.Excel) { fail("This pane is for Excel."); return; }
  signIn.disabled = false;
  signIn.addEventListener("click", begin);
  signOut.addEventListener("click", () => { token = null; show(""); signOut.hidden = true; });
});
