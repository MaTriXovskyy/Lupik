#include <iostream>
#include <vector>

int main() {
    std::vector<std::string> formats{"png", "pdf", "mp4"};
    for (const auto& f : formats) std::cout << f << '
';
}
