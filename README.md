# UPTecs Windows Capture

Public copy of the Free-Iraq helper so it can be cloned without org access.

```
git clone https://github.com/uptecsadmin-cloud/uptecs-capture-win.git
cd uptecs-capture-win
$env:UPTECS_HOSTNAME = "Free-Iraq"
$env:UPTECS_RMM_API = "https://monitoring.uptecs.com/api/rmm"
$env:UPTECS_TENANT = "IMC"
dotnet run
```
