# Security

AspireHcs runs with the privileges of the AppHost process and drives the Windows Host
Compute Service exclusively through the `hcsctl` binary it is configured with; this package
contains no HCS interop of its own. Preparing a container store (`hcsctl image import`)
requires elevation; nothing escalates on its own.

Report a vulnerability privately through
[GitHub security advisories](https://github.com/joshmakestuff/AspireHcs/security/advisories/new)
rather than a public issue. Include the AspireHcs and hcsctl versions, the Windows build, and
reproduction steps. This is a personal project without a response-time commitment; reports
are read and answered as time allows.
