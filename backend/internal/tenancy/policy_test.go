package tenancy

import (
	"errors"
	"strings"
	"testing"
)

func TestInvitePermissions(t *testing.T) {
	cases := []struct {
		actor, invited Role
		space          Type
		want           error
	}{
		{Owner, Admin, Family, nil},
		{Owner, Member, Organization, nil},
		{Admin, Admin, Organization, nil},
		{Admin, Member, Family, nil},
		{Admin, Owner, Family, ErrInvalidRole},
		{Owner, Owner, Family, ErrInvalidRole},
		{Member, Member, Family, ErrForbidden},
		{Owner, Member, Personal, ErrPersonalSpace},
	}
	for _, c := range cases {
		if got := CanInvite(c.actor, c.invited, c.space); !errors.Is(got, c.want) && got != c.want {
			t.Errorf("%s invites %s into %s: got %v, want %v", c.actor, c.invited, c.space, got, c.want)
		}
	}
}

func TestRemovePermissions(t *testing.T) {
	allowed := map[[2]Role]bool{
		{Owner, Owner}: true, {Owner, Admin}: true, {Owner, Member}: true,
		{Admin, Member}: true,
	}
	for _, actor := range []Role{Owner, Admin, Member} {
		for _, target := range []Role{Owner, Admin, Member} {
			err := CanRemove(actor, target, false)
			if (err == nil) != allowed[[2]Role{actor, target}] {
				t.Errorf("%s removes %s: %v", actor, target, err)
			}
			if CanRemove(actor, target, true) != nil {
				t.Errorf("%s must be able to leave", actor)
			}
		}
	}
}

func TestOnlyOwnersChangeRolesAndDelete(t *testing.T) {
	for _, r := range []Role{Admin, Member} {
		if CanChangeRole(r) == nil || CanDelete(r) {
			t.Errorf("%s must not change roles or delete", r)
		}
	}
	if CanChangeRole(Owner) != nil || !CanDelete(Owner) || !CanManage(Admin) || CanManage(Member) {
		t.Fatal("owner/admin permissions wrong")
	}
}

func TestNamesAndParsing(t *testing.T) {
	if name, err := CleanName("  ", Personal); err != nil || name != DefaultPersonalName {
		t.Fatalf("personal default: %q %v", name, err)
	}
	if _, err := CleanName("", Family); !errors.Is(err, ErrInvalidName) {
		t.Fatal("family needs a name")
	}
	if name, _ := CleanName(" The\tParks \u0007", Family); name != "TheParks" {
		t.Fatalf("control characters must be removed: %q", name)
	}
	if _, err := CleanName(strings.Repeat("🏡", 101), Family); !errors.Is(err, ErrInvalidName) {
		t.Fatal("101 characters accepted")
	}
	if name, err := CleanName(strings.Repeat("🏡", 100), Organization); err != nil || name == "" {
		t.Fatal("100 emoji must be accepted")
	}
	if _, err := ParseType("Family"); err != nil {
		t.Fatal(err)
	}
	if _, err := ParseType("team"); !errors.Is(err, ErrInvalidType) {
		t.Fatal("unknown type accepted")
	}
	if _, err := ParseRole("superuser"); !errors.Is(err, ErrInvalidRole) {
		t.Fatal("unknown role accepted")
	}
}
