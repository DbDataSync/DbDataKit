// Where the identity home is and which client this pane is. Register the client first (on the machine of the home):
//   dbdatakit identity client-add --name excel-hello --public --redirect https://localhost:3000/auth-done.html
// then put the id it prints here. The home must answer CORS for this pane's origin on /identity/token and /identity/userinfo:
// it does not today (XL02 adds it); see docs/excel-addin-spike.md, "Sign-in".
window.HELLO = {
  home: "https://localhost:8482",
  clientId: "excel-hello",
  scope: "openid profile email",
};
