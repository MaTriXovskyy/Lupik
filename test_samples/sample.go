package main

import "fmt"

func main() {
	for i, f := range []string{"png", "pdf", "mp4"} {
		fmt.Printf("%d: %s
", i+1, f)
	}
}
