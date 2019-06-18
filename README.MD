This buildpack can bootstrap Windows Services without requiring that they be refactored to console app. 

It provides a basic SCM emulation and redirects all native calls to it. It also rewrites calls to Windows Event Log to be sent to console instead.