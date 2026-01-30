# ISO creation (oscdimg)

For creating output ISO files, the application uses **oscdimg.exe**.

## Bundling with the application

Place **oscdimg.exe** in this folder (or in the application output folder next to the .exe). It will be found automatically.

## Where to get oscdimg.exe

1. Install the **Windows ADK** (Assessment and Deployment Kit) from Microsoft.
2. oscdimg.exe is under:
   - `Program Files\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe`
   - or the `x86` variant under the same path.

You can copy `oscdimg.exe` from that location into this `Tools` folder so that published builds include it and no separate ADK install is required on the target machine.
