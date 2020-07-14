# AndroidBackupHelper

key feature: pack an Android backup "apps" dir with proper format (i.e. generate a valid backup.ab)

Implement the tar format same as Android backup service (line by line translate from C++ to C#, latest version of Android 6), and test against multiple backups.

Currently password is not supported.

also: show *.ab contents or unpack it to a folder

-  unpack:     Unpack an android backup file (*.ab).

-  show:       List content of an android backup file (*.ab).

-  pack:       pack a backup "apps" dir.

-  help:       Display more information on a specific command.

-  version:    Display version information.
