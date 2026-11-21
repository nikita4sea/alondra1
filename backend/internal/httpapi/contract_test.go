package httpapi_test

import (
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// The OpenAPI file (packages/api-contract/openapi.yaml) is the source of truth; these checks keep the router and the
// error codes in step with it.
func openAPI(t *testing.T) string {
	t.Helper()
	data, err := os.ReadFile(filepath.Join("..", "..", "..", "packages", "api-contract", "openapi.yaml"))
	if err != nil {
		t.Fatal(err)
	}
	return string(data)
}

func TestEveryDocumentedOperationIsRouted(t *testing.T) {
	f := newFixture(t)
	spec := openAPI(t)
	pathPattern := regexp.MustCompile(`(?m)^  (/v1/[^:\s]+):\s*$`)
	methodPattern := regexp.MustCompile(`(?m)^    (get|post|put|patch|delete):\s*$`)
	paths := pathPattern.FindAllStringSubmatchIndex(spec, -1)
	if len(paths) < 8 {
		t.Fatalf("expected the documented paths, found %d", len(paths))
	}
	for i, match := range paths {
		path := spec[match[2]:match[3]]
		end := len(spec)
		if i+1 < len(paths) {
			end = paths[i+1][0]
		}
		for _, method := range methodPattern.FindAllStringSubmatch(spec[match[1]:end], -1) {
			r := f.do(t, strings.ToUpper(method[1]), path, "", nil)
			if errorCode(r) == "NOT_FOUND" || r.status == http.StatusMethodNotAllowed {
				t.Errorf("%s %s is documented but not routed (%d)", strings.ToUpper(method[1]), path, r.status)
			}
		}
	}
}

func TestErrorCodesInCodeAreDocumented(t *testing.T) {
	spec := openAPI(t)
	source, err := os.ReadFile("httpapi.go")
	if err != nil {
		t.Fatal(err)
	}
	for _, match := range regexp.MustCompile(`http\.Status\w+, "([A-Z_]+)"`).FindAllStringSubmatch(string(source), -1) {
		if !strings.Contains(spec, match[1]) {
			t.Errorf("error code %s is not in the OpenAPI Error enum", match[1])
		}
	}
}
