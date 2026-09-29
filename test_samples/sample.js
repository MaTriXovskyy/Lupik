const items = ['png', 'pdf', 'mp4'];

function describe(list) {
  return list.map((ext, i) => `${i + 1}. ${ext}`).join('
');
}

console.log(describe(items));
