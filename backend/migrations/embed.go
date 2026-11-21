// Package migrations holds the forward-only SQL migrations, applied in file-name order before the API serves traffic.
package migrations

import "embed"

//go:embed *.sql
var FS embed.FS
