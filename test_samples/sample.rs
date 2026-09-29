fn main() {
    let formats = ["png", "pdf", "mp4"];
    for (i, f) in formats.iter().enumerate() {
        println!("{}: {}", i + 1, f);
    }
}
